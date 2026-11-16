import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, makeTestApp, addMember, type TestApp } from "./harness.js";
import {
  projects,
  attachments,
  attachmentReceipts,
  memberships,
  projectAssignments,
  rolePermissions,
  session,
} from "../src/db/schema/index.js";
import type { AttachmentStore } from "../src/attachment-store.js";
import { AppError } from "../src/errors.js";
import { MAX_ATTACHMENT_BYTES } from "../src/attachment-contract.js";
let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
class Store implements AttachmentStore {
  objects = new Map<string, Buffer>();
  puts = 0;
  removals = 0;
  unavailable = false;
  async check() {
    if (this.unavailable) throw new AppError(503, "FILE_STORAGE_UNAVAILABLE", "Unavailable");
  }
  async put(key: string, bytes: Buffer) {
    await this.check();
    this.puts++;
    this.objects.set(key, Buffer.from(bytes));
  }
  async get(key: string) {
    await this.check();
    const value = this.objects.get(key);
    if (!value) throw new AppError(503, "FILE_STORAGE_UNAVAILABLE", "Unavailable");
    return value;
  }
  async remove(key: string) {
    this.removals++;
    this.objects.delete(key);
  }
}
async function setup() {
  const store = new Store();
  t = await makeTestApp({}, store);
  const c = await bootstrapCompany(t);
  const [p] = await t.db
    .insert(projects)
    .values({ organizationId: c.organizationId, name: "File project", createdBy: c.userId })
    .returning();
  const pm = await addMember(t, c.session, "pm-files@example.test", c.roleId["Project Manager"]!);
  const staff = await addMember(t, c.session, "staff-files@example.test", c.roleId.Staff!);
  const rows = await t.db.select().from(memberships);
  const manager = rows.find((m) => m.authUserId === pm.userId)!,
    worker = rows.find((m) => m.authUserId === staff.userId)!;
  await t.db.insert(projectAssignments).values([
    { organizationId: c.organizationId, projectId: p!.id, membershipId: manager.id },
    { organizationId: c.organizationId, projectId: p!.id, membershipId: worker.id },
  ]);
  return { ...c, store, project: p!, pm, staff, manager, worker };
}
const input = () => ({
  id: randomUUID(),
  operationId: randomUUID(),
  name: "demo.txt",
  contentType: "text/plain",
  contentBase64: Buffer.from("Fictional attachment for testing.").toString("base64"),
});
const call = (
  c: Awaited<ReturnType<typeof setup>>,
  method: "POST" | "DELETE" | "GET",
  extra = "",
  payload?: object,
  headers = c.pm.session.headers,
) => t!.app.inject({ method, url: "/v1/projects/" + c.project.id + "/files" + extra, headers, payload });
describe("private project attachments", () => {
  it("uses canonical UUID paths for blob writes and database reads", async () => {
    const c = await setup(),
      body = input();
    const response = await t!.app.inject({
      method: "POST",
      url: "/v1/projects/" + c.project.id.toUpperCase() + "/files",
      headers: c.pm.session.headers,
      payload: { ...body, id: body.id.toUpperCase() },
    });
    expect(response.statusCode).toBe(200);
    expect((await call(c, "GET", "/" + body.id + "/content")).statusCode).toBe(200);
  });

  it("uploads once with stable concurrent retries and reads verified bytes without exposing object keys", async () => {
    const c = await setup(),
      body = input();
    const [a, b] = await Promise.all([call(c, "POST", "", body), call(c, "POST", "", body)]);
    expect(a.statusCode).toBe(200);
    expect(b.json()).toEqual(a.json());
    expect(c.store.puts).toBe(1);
    expect(a.json().file).toMatchObject({ id: body.id, name: "demo.txt", version: 1 });
    expect(a.json().file).not.toHaveProperty("objectKey");
    expect(await t!.db.select().from(attachmentReceipts)).toHaveLength(1);
    const read = await call(c, "GET", "/" + body.id + "/content", undefined, c.staff.session.headers);
    expect(read.statusCode).toBe(200);
    expect(read.json().contentBase64).toBe(body.contentBase64);
    expect(read.headers["cache-control"]).toBe("no-store");
    const live = await t!.app.inject({
      url: "/v1/mobile/projects/live?fileProjectId=" + c.project.id,
      headers: c.staff.session.headers,
    });
    expect(live.json().filePage.files).toHaveLength(1);
    expect((await call(c, "POST", "", { ...body, name: "other.txt" })).json().error.code).toBe(
      "OPERATION_ID_REUSED",
    );
    expect((await call(c, "POST", "", { ...body, operationId: randomUUID() })).json().error.code).toBe(
      "FILE_ID_IN_USE",
    );
    expect(c.store.puts).toBe(1);
  });
  it("retains hidden file/object history, requires confirmation, and prevents upload resurrection", async () => {
    const c = await setup(),
      body = input();
    await call(c, "POST", "", body);
    expect(
      (
        await call(c, "DELETE", "/" + body.id, {
          operationId: randomUUID(),
          expectedVersion: 1,
          confirmation: "delete",
        })
      ).statusCode,
    ).toBe(400);
    const stale = { operationId: randomUUID(), expectedVersion: 2, confirmation: "DELETE" };
    const conflict = await call(c, "DELETE", "/" + body.id, stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict.current.version).toBe(1);
    const remove = { ...stale, operationId: randomUUID(), expectedVersion: 1 };
    const deleted = await call(c, "DELETE", "/" + body.id, remove);
    expect(deleted.statusCode).toBe(200);
    expect((await call(c, "DELETE", "/" + body.id, remove)).json()).toEqual(deleted.json());
    expect((await call(c, "GET")).json().files).toEqual([]);
    expect((await call(c, "GET", "/" + body.id + "/content")).statusCode).toBe(404);
    expect((await call(c, "POST", "", body)).statusCode).toBe(404);
    expect((await t!.db.select().from(attachments))[0]?.deletedAt).not.toBeNull();
    expect(c.store.objects.size).toBe(1);
    expect(c.store.removals).toBe(0);
    await expect(
      t!.pg.query("update studio.attachment_receipts set request_hash=request_hash"),
    ).rejects.toThrow("immutable");
  });
  it("denies readonly uploads, cross-project reads and lost-assignment downloads or receipt replays", async () => {
    const c = await setup(),
      body = input();
    expect((await call(c, "POST", "", body, c.staff.session.headers)).statusCode).toBe(403);
    await call(c, "POST", "", body);
    const [other] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Other", createdBy: c.userId })
      .returning();
    expect(
      (
        await t!.app.inject({
          url: "/v1/projects/" + other!.id + "/files/" + body.id + "/content",
          headers: c.session.headers,
        })
      ).statusCode,
    ).toBe(404);
    await t!.db.delete(projectAssignments).where(eq(projectAssignments.membershipId, c.manager.id));
    expect((await call(c, "POST", "", body)).statusCode).toBe(404);
    expect((await call(c, "GET", "/" + body.id + "/content")).statusCode).toBe(404);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.worker.id));
    expect(
      (await call(c, "GET", "/" + body.id + "/content", undefined, c.staff.session.headers)).statusCode,
    ).toBe(403);
  });
  it("rejects unsafe names unsupported or disguised formats invalid base64 and oversized files", async () => {
    const c = await setup();
    for (const extra of [
      { name: "../demo.txt" },
      { name: "demo.html" },
      { name: "bad.exe", contentType: "application/octet-stream" },
      { name: "demo.pdf", contentType: "application/pdf" },
      { name: "demo.png", contentType: "image/png" },
      { contentBase64: "bad==" },
      { contentBase64: Buffer.from([0, 1, 2]).toString("base64") },
      { contentBase64: Buffer.alloc(MAX_ATTACHMENT_BYTES + 1, 65).toString("base64") },
    ])
      expect((await call(c, "POST", "", { ...input(), ...extra })).statusCode).toBe(400);
    expect(c.store.puts).toBe(0);
    expect(await t!.db.select().from(attachments)).toHaveLength(0);
  });
  it("rolls back metadata receipts and own newly uploaded blob when the audit write fails", async () => {
    const c = await setup();
    await t!.pg
      .exec(`CREATE FUNCTION audit.reject_file_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.action='file.upload' THEN RAISE EXCEPTION 'test audit rejection'; END IF; RETURN NEW; END $$;
  CREATE TRIGGER reject_file_test BEFORE INSERT ON audit.events FOR EACH ROW EXECUTE FUNCTION audit.reject_file_test();`);
    expect((await call(c, "POST", "", input())).statusCode).toBe(500);
    expect(c.store.puts).toBe(1);
    expect(c.store.removals).toBe(1);
    expect(c.store.objects.size).toBe(0);
    expect(await t!.db.select().from(attachments)).toHaveLength(0);
    expect(await t!.db.select().from(attachmentReceipts)).toHaveLength(0);
  });
  it("fails closed when storage is unavailable or bytes fail the metadata hash", async () => {
    const c = await setup(),
      body = input();
    await call(c, "POST", "", body);
    const key = [...c.store.objects.keys()][0]!;
    c.store.objects.set(key, Buffer.from("tampered"));
    expect((await call(c, "GET", "/" + body.id + "/content")).json().error.code).toBe("FILE_INTEGRITY_ERROR");
    c.store.unavailable = true;
    expect((await call(c, "GET", "/" + body.id + "/content")).statusCode).toBe(503);
    const status = await t!.app.inject({ url: "/v1/files/status", headers: c.pm.session.headers });
    expect(status.json().available).toBe(false);
  });
  it("requires a fresh session for deletion and stops writes after permission loss", async () => {
    const c = await setup(),
      body = input();
    await call(c, "POST", "", body);
    await t!.db
      .update(session)
      .set({ createdAt: new Date(Date.now() - 10 * 60 * 1000) })
      .where(eq(session.userId, c.pm.userId));
    expect(
      (
        await call(c, "DELETE", "/" + body.id, {
          operationId: randomUUID(),
          expectedVersion: 1,
          confirmation: "DELETE",
        })
      ).json().error.code,
    ).toBe("REAUTH_REQUIRED");
    await t!.db.delete(rolePermissions).where(eq(rolePermissions.permissionKey, "files.write"));
    expect((await call(c, "POST", "", body)).statusCode).toBe(403);
  });
});
