import { createHash } from "node:crypto";
import { TextDecoder } from "node:util";
import { and, eq, gt, isNull, sql } from "drizzle-orm";
import { attachments, attachmentReceipts, projects } from "../db/schema/index.js";
import { recordAudit } from "../audit.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import type { AttachmentStore } from "../attachment-store.js";
import { AppError } from "../errors.js";
import {
  attachmentView,
  attachmentResult,
  MAX_ATTACHMENT_BYTES,
  type AttachmentUploadInput,
  type AttachmentDeleteInput,
  type AttachmentResult,
} from "../attachment-contract.js";
import { withProjectContext } from "./context.js";
const fields = {
  id: attachments.id,
  projectId: attachments.projectId,
  name: attachments.name,
  contentType: attachments.contentType,
  size: attachments.size,
  sha256: attachments.sha256,
  version: attachments.version,
};
const visible = (by: Principal, projectId: string) =>
  and(
    eq(attachments.organizationId, by.organizationId),
    eq(attachments.projectId, projectId),
    isNull(attachments.deletedAt),
    sql`studio.project_allowed(${projectId}::uuid,'projects.read')`,
  );
export function validateAttachment(input: AttachmentUploadInput): Buffer {
  const bytes = Buffer.from(input.contentBase64, "base64");
  const invalid = () => {
    throw new AppError(
      400,
      "INVALID_FILE",
      "Choose a supported file up to 5 MiB with its matching extension and format.",
    );
  };
  if (
    bytes.length < 1 ||
    bytes.length > MAX_ATTACHMENT_BYTES ||
    bytes.toString("base64") !== input.contentBase64
  )
    invalid();
  const ext = input.name.toLowerCase().split(".").at(-1);
  switch (input.contentType) {
    case "application/pdf":
      if (ext !== "pdf" || bytes.subarray(0, 5).toString() !== "%PDF-") invalid();
      break;
    case "image/png":
      if (ext !== "png" || !bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])))
        invalid();
      break;
    case "image/jpeg":
      if (
        !["jpg", "jpeg"].includes(ext ?? "") ||
        bytes[0] !== 255 ||
        bytes[1] !== 216 ||
        bytes.at(-2) !== 255 ||
        bytes.at(-1) !== 217
      )
        invalid();
      break;
    case "image/webp":
      if (
        ext !== "webp" ||
        bytes.subarray(0, 4).toString() !== "RIFF" ||
        bytes.subarray(8, 12).toString() !== "WEBP"
      )
        invalid();
      break;
    case "text/plain":
    case "text/csv":
      if (ext !== (input.contentType === "text/plain" ? "txt" : "csv") || bytes.includes(0)) invalid();
      try {
        new TextDecoder("utf-8", { fatal: true }).decode(bytes);
      } catch {
        invalid();
      }
      break;
  }
  return bytes;
}
export function listAttachments(db: Db, by: Principal, projectId: string, after?: string) {
  return withProjectContext(db, by, async (tx) => {
    const [project] = await tx
      .select({ id: projects.id })
      .from(projects)
      .where(
        and(
          eq(projects.id, projectId),
          eq(projects.organizationId, by.organizationId),
          isNull(projects.deletedAt),
          sql`studio.project_allowed(${projectId}::uuid,'projects.read')`,
        ),
      )
      .limit(1);
    if (!project) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
    const rows = await tx
      .select(fields)
      .from(attachments)
      .where(and(visible(by, projectId), after ? gt(attachments.id, after) : undefined))
      .orderBy(attachments.id)
      .limit(26);
    const files = rows.slice(0, 25);
    return { projectId, files, nextCursor: rows.length > 25 ? files.at(-1)!.id : null };
  });
}
export function readAttachment(
  db: Db,
  store: AttachmentStore | undefined,
  by: Principal,
  projectId: string,
  id: string,
) {
  if (!store)
    throw new AppError(503, "FILES_NOT_CONFIGURED", "Private attachment storage is not configured.");
  return withProjectContext(db, by, async (tx) => {
    const [row] = await tx
      .select({ ...fields, objectKey: attachments.objectKey })
      .from(attachments)
      .where(and(visible(by, projectId), eq(attachments.id, id)))
      .limit(1);
    if (!row) throw new AppError(404, "FILE_NOT_FOUND", "No such file.");
    const bytes = await store.get(row.objectKey);
    if (bytes.length !== row.size || createHash("sha256").update(bytes).digest("hex") !== row.sha256)
      throw new AppError(503, "FILE_INTEGRITY_ERROR", "The file could not be verified.");
    const [current] = await tx
      .select({ id: attachments.id })
      .from(attachments)
      .where(and(visible(by, projectId), eq(attachments.id, id)))
      .limit(1);
    if (!current) throw new AppError(404, "FILE_NOT_FOUND", "File access changed.");
    return { file: attachmentView.parse(row), contentBase64: bytes.toString("base64") };
  });
}
export function mutateAttachment(
  db: Db,
  store: AttachmentStore | undefined,
  by: Principal,
  projectId: string,
  id: string,
  input: AttachmentUploadInput | AttachmentDeleteInput,
  requestId: string,
  action: "upload" | "delete",
): Promise<AttachmentResult> {
  const bytes = action === "upload" ? validateAttachment(input as AttachmentUploadInput) : null;
  const sha = bytes ? createHash("sha256").update(bytes).digest("hex") : null;
  if (action === "upload" && !store)
    throw new AppError(503, "FILES_NOT_CONFIGURED", "Private attachment storage is not configured.");
  return withProjectContext(db, by, async (tx) => {
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":attachment:" + input.operationId},0))`,
    );
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId.toLowerCase() + ":" + projectId.toLowerCase() + ":file:" + id.toLowerCase()},0))`,
    );
    const authority = (await tx.execute(
      sql`select studio.file_action_allowed(${projectId}::uuid,${id}::uuid,${action}) allowed`,
    )) as { rows: { allowed: boolean }[] };
    if (authority.rows[0]?.allowed !== true)
      throw new AppError(404, "FILE_NOT_FOUND", "No such file action.");
    const upload = input as AttachmentUploadInput,
      remove = input as AttachmentDeleteInput;
    const hash = createHash("sha256")
      .update(
        JSON.stringify([
          action,
          projectId,
          id,
          action === "upload"
            ? [upload.name, upload.contentType, bytes!.length, sha]
            : remove.expectedVersion,
        ]),
      )
      .digest("hex");
    const [receipt] = await tx
      .select()
      .from(attachmentReceipts)
      .where(
        and(
          eq(attachmentReceipts.organizationId, by.organizationId),
          eq(attachmentReceipts.userId, by.userId),
          eq(attachmentReceipts.operationId, input.operationId),
          sql`studio.file_action_allowed(${attachmentReceipts.projectId},${attachmentReceipts.fileId},${attachmentReceipts.action})`,
        ),
      )
      .limit(1);
    if (receipt) {
      if (receipt.requestHash !== hash)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation for different values.");
      if (action === "upload") {
        const [active] = await tx
          .select({ id: attachments.id })
          .from(attachments)
          .where(and(visible(by, projectId), eq(attachments.id, id)))
          .limit(1);
        if (!active) throw new AppError(404, "FILE_NOT_FOUND", "File no longer available.");
      }
      return attachmentResult.parse(receipt.result);
    }
    const key =
      by.organizationId.toLowerCase() + "/" + projectId.toLowerCase() + "/" + id.toLowerCase() + "/" + sha;
    let ownsNewObject = false;
    try {
      let result: AttachmentResult;
      if (action === "upload") {
        const reserved = (await tx.execute(
          sql`select studio.file_id_reserved(${projectId}::uuid,${id}::uuid) reserved`,
        )) as { rows: { reserved: boolean }[] };
        if (reserved.rows[0]?.reserved)
          throw new AppError(409, "FILE_ID_IN_USE", "This file id is reserved.");
        ownsNewObject = true;
        await store!.put(key, bytes!, upload.contentType);
        const rows = (await tx.execute(
          sql`select studio.register_attachment(${projectId}::uuid,${id}::uuid,${upload.name},${upload.contentType},${bytes!.length}::integer,${sha}) result`,
        )) as { rows: { result: unknown }[] };
        if (!rows.rows[0]?.result)
          throw new AppError(409, "FILE_CHANGED", "File authority or identity changed.");
        result = attachmentResult.parse({
          outcome: "uploaded",
          operationId: input.operationId,
          file: rows.rows[0].result,
        });
      } else {
        const rows = (await tx.execute(
          sql`select studio.delete_attachment(${projectId}::uuid,${id}::uuid,${remove.expectedVersion}::integer) result`,
        )) as { rows: { result: Record<string, unknown> | null }[] };
        if (!rows.rows[0]?.result) throw new AppError(404, "FILE_NOT_FOUND", "File no longer available.");
        result = attachmentResult.parse({ ...rows.rows[0].result, operationId: input.operationId });
      }
      const inserted = await tx
        .insert(attachmentReceipts)
        .values({
          organizationId: by.organizationId,
          userId: by.userId,
          operationId: input.operationId,
          projectId,
          fileId: id,
          action,
          requestHash: hash,
          result,
        })
        .onConflictDoNothing()
        .returning({ id: attachmentReceipts.operationId });
      if (!inserted.length) throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id.");
      if (result.outcome !== "conflict")
        await recordAudit(tx, {
          organizationId: by.organizationId,
          actor: by.userId,
          action: "file." + action,
          resourceType: "file",
          resourceId: id,
          requestId,
          detail: { projectId, operationId: input.operationId },
        });
      return result;
    } catch (error) {
      // Still holding the per-file transaction lock; only a new, unregistered upload can be removed here.
      if (ownsNewObject)
        try {
          await store!.remove(key);
        } catch {
          /* Private orphan reconciliation is required after a storage outage. */
        }
      throw error;
    }
  });
}
