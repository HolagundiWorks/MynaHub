import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, makeTestApp, addMember, type TestApp } from "./harness.js";
import {
  projects,
  clients,
  memberships,
  roles,
  rolePermissions,
  clientMutationReceipts,
} from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";
let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
const fields = (name = "New client") => ({
  name,
  kind: "individual",
  contactPerson: "Demo person",
  email: "contact@example.test",
  phone: "",
  address: "",
  correspondenceAddress: "",
  preferredCommunication: "email",
  notes: "",
});
const input = () => ({ ...fields(), id: randomUUID(), operationId: randomUUID() });
const call = (
  headers: Record<string, string>,
  method: "POST" | "PATCH" | "DELETE" | "PUT" | "GET",
  path: string,
  payload?: object,
) => t!.app.inject({ method, url: path, headers, payload });
async function setup() {
  t = await makeTestApp();
  return bootstrapCompany(t);
}
describe("online client mutations", () => {
  it("creates once on concurrent retry with server-owned company and immutable receipts", async () => {
    const c = await setup(),
      body = input();
    const [a, b] = await Promise.all([
      call(c.session.headers, "POST", "/v1/clients", body),
      call(c.session.headers, "POST", "/v1/clients", body),
    ]);
    expect(a.statusCode).toBe(200);
    expect(b.json()).toEqual(a.json());
    expect(a.json()).toMatchObject({
      outcome: "created",
      client: { id: body.id, version: 1, name: "New client" },
    });
    expect(await t!.db.select().from(clientMutationReceipts)).toHaveLength(1);
    expect((await t!.db.select().from(clients))[0]?.organizationId).toBe(c.organizationId);
    expect(
      (await call(c.session.headers, "POST", "/v1/clients", { ...body, name: "Other" })).json().error.code,
    ).toBe("OPERATION_ID_REUSED");
    expect(
      (await call(c.session.headers, "POST", "/v1/clients", { ...body, operationId: randomUUID() })).json()
        .error.code,
    ).toBe("CLIENT_ID_IN_USE");
    await expect(
      t!.pg.query("update studio.client_mutation_receipts set request_hash=request_hash"),
    ).rejects.toThrow("immutable");
  });
  it("version-checks edits and deletion, replays conflicts, and retains hidden tombstones", async () => {
    const c = await setup(),
      body = input();
    await call(c.session.headers, "POST", "/v1/clients", body);
    const edit = { ...fields("Changed"), operationId: randomUUID(), expectedVersion: 1 };
    const saved = await call(c.session.headers, "PATCH", "/v1/clients/" + body.id, edit);
    expect(saved.statusCode).toBe(200);
    expect(saved.json().client.version).toBe(2);
    expect((await call(c.session.headers, "PATCH", "/v1/clients/" + body.id, edit)).json()).toEqual(
      saved.json(),
    );
    const stale = { operationId: randomUUID(), expectedVersion: 1, confirmation: "DELETE" };
    const conflict = await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict.current.name).toBe("Changed");
    expect(
      (await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, stale)).json().conflict,
    ).toEqual(conflict.json().conflict);
    const remove = { ...stale, operationId: randomUUID(), expectedVersion: 2 };
    const deleted = await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, remove);
    expect(deleted.statusCode).toBe(200);
    expect(deleted.json().version).toBe(3);
    expect((await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, remove)).json()).toEqual(
      deleted.json(),
    );
    expect((await call(c.session.headers, "GET", "/v1/clients/" + body.id)).statusCode).toBe(404);
    expect((await call(c.session.headers, "POST", "/v1/clients", body)).statusCode).toBe(404);
    expect((await t!.db.select().from(clients))[0]?.deletedAt).not.toBeNull();
  });
  it("links only current same-company contacts and blocks deletion until all active projects are unlinked", async () => {
    const c = await setup(),
      body = input();
    await call(c.session.headers, "POST", "/v1/clients", body);
    const [p] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Link project", createdBy: c.userId })
      .returning();
    const url = "/v1/projects/" + p!.id + "/client",
      link = { operationId: randomUUID(), expectedVersion: 1, clientId: body.id };
    expect((await call(c.session.headers, "GET", url)).json()).toMatchObject({ clientId: null, version: 1 });
    const linked = await call(c.session.headers, "PUT", url, link);
    expect(linked.statusCode).toBe(200);
    expect(linked.json().project).toMatchObject({ clientId: body.id, clientName: body.name, version: 2 });
    expect((await call(c.session.headers, "PUT", url, link)).json()).toEqual(linked.json());
    expect(
      (
        await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, {
          operationId: randomUUID(),
          expectedVersion: 1,
          confirmation: "DELETE",
        })
      ).json().error.code,
    ).toBe("CLIENT_IN_USE");
    const conflict = await call(c.session.headers, "PUT", url, {
      ...link,
      clientId: null,
      operationId: randomUUID(),
    });
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict.current.clientId).toBe(body.id);
    expect(
      (
        await call(c.session.headers, "PUT", url, {
          clientId: null,
          expectedVersion: 2,
          operationId: randomUUID(),
        })
      ).statusCode,
    ).toBe(200);
    expect(
      (
        await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, {
          operationId: randomUUID(),
          expectedVersion: 1,
          confirmation: "DELETE",
        })
      ).statusCode,
    ).toBe(200);
    expect(
      (
        await call(c.session.headers, "PUT", url, {
          clientId: body.id,
          expectedVersion: 3,
          operationId: randomUUID(),
        })
      ).statusCode,
    ).toBe(404);
  });
  it("denies cross-company clients, readonly and assigned writers, inactive members and permission-loss receipt replay", async () => {
    const c = await setup(),
      body = input();
    await call(c.session.headers, "POST", "/v1/clients", body);
    const staff = await addMember(t!, c.session, "staff-client@example.test", c.roleId.Staff!);
    expect((await call(staff.session.headers, "POST", "/v1/clients", input())).statusCode).toBe(403);
    await t!.db.insert(rolePermissions).values({ roleId: c.roleId.Staff!, permissionKey: "clients.write" });
    expect((await call(staff.session.headers, "POST", "/v1/clients", input())).statusCode).toBe(403);
    const other = await seedOrganization(t!.db, { name: "Other", slug: "other" });
    const [foreign] = await t!.db
      .insert(clients)
      .values({ organizationId: other.organizationId, name: "Foreign" })
      .returning();
    expect(
      (
        await call(c.session.headers, "PATCH", "/v1/clients/" + foreign!.id, {
          ...fields(),
          operationId: randomUUID(),
          expectedVersion: 1,
        })
      ).statusCode,
    ).toBe(404);
    const [p] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Own", createdBy: c.userId })
      .returning();
    expect(
      (
        await call(c.session.headers, "PUT", "/v1/projects/" + p!.id + "/client", {
          clientId: foreign!.id,
          operationId: randomUUID(),
          expectedVersion: 1,
        })
      ).statusCode,
    ).toBe(404);
    await t!.db.delete(rolePermissions).where(eq(rolePermissions.permissionKey, "clients.write"));
    expect((await call(c.session.headers, "POST", "/v1/clients", body)).statusCode).toBe(403);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.authUserId, c.userId));
    expect((await call(c.session.headers, "GET", "/v1/clients")).statusCode).toBe(403);
  });
  it("rejects invalid fields and requires explicit deletion confirmation and organization scope", async () => {
    const c = await setup();
    for (const extra of [
      { name: " " },
      { kind: "invalid" },
      { notes: "x".repeat(4001) },
      { email: "x".repeat(255) },
      { organizationId: c.organizationId },
    ])
      expect(
        (await call(c.session.headers, "POST", "/v1/clients", { ...input(), ...extra })).statusCode,
      ).toBe(400);
    const body = input();
    await call(c.session.headers, "POST", "/v1/clients", body);
    expect(
      (
        await call(c.session.headers, "DELETE", "/v1/clients/" + body.id, {
          operationId: randomUUID(),
          expectedVersion: 1,
          confirmation: "delete",
        })
      ).statusCode,
    ).toBe(400);
    expect(
      (
        await call(c.session.headers, "POST", "/v1/roles", {
          name: "Assigned client writer",
          scope: "assigned",
          description: "",
          permissions: ["clients.read", "clients.write", "projects.read"],
        })
      ).statusCode,
    ).toBe(400);
    await t!.db.update(roles).set({ scope: "assigned" }).where(eq(roles.id, c.roleId.Owner!));
    expect((await call(c.session.headers, "POST", "/v1/clients", input())).statusCode).toBe(403);
  });
});
