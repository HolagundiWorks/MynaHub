import { createHash } from "node:crypto";
import { and, eq, gt, isNull, sql } from "drizzle-orm";
import { siteRecords, siteMutationReceipts, projects } from "../db/schema/index.js";
import { recordAudit } from "../audit.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import { AppError } from "../errors.js";
import {
  siteFields,
  siteMutationResult,
  type SiteMutationInput,
  type SiteMutationResult,
} from "../site-contract.js";
import { withProjectContext, lockOrganization } from "./context.js";
const fields = {
  id: siteRecords.id,
  projectId: siteRecords.projectId,
  kind: siteRecords.kind,
  recordDate: siteRecords.recordDate,
  title: siteRecords.title,
  details: siteRecords.details,
  location: siteRecords.location,
  assignedTo: siteRecords.assignedTo,
  dueDate: siteRecords.dueDate,
  status: siteRecords.status,
  resolvedDate: siteRecords.resolvedDate,
  version: siteRecords.version,
};
export function listSiteRecords(db: Db, by: Principal, projectId: string, after?: string) {
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
      .from(siteRecords)
      .where(
        and(
          eq(siteRecords.organizationId, by.organizationId),
          eq(siteRecords.projectId, projectId),
          isNull(siteRecords.deletedAt),
          sql`studio.project_allowed(${projectId}::uuid,'projects.read')`,
          after ? gt(siteRecords.id, after) : undefined,
        ),
      )
      .orderBy(siteRecords.id)
      .limit(26);
    const records = rows.slice(0, 25);
    return { projectId, records, nextCursor: rows.length > 25 ? records.at(-1)!.id : null };
  });
}
export function mutateSiteRecord(
  db: Db,
  by: Principal,
  projectId: string,
  id: string,
  input: SiteMutationInput,
  requestId: string,
  action: "create" | "edit" | "delete",
): Promise<SiteMutationResult> {
  return withProjectContext(db, by, async (tx) => {
    await lockOrganization(tx, by.organizationId);
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":site:" + input.operationId},0))`,
    );
    const authority = (await tx.execute(
      sql`select studio.site_action_allowed(${projectId}::uuid,${id}::uuid,${action}) allowed`,
    )) as { rows: { allowed: boolean }[] };
    if (authority.rows[0]?.allowed !== true)
      throw new AppError(404, "SITE_RECORD_NOT_FOUND", "No such site action.");
    const payload = action === "delete" ? {} : siteFields.parse(input),
      expected = "expectedVersion" in input ? input.expectedVersion : null;
    const hash = createHash("sha256")
      .update(JSON.stringify([action, projectId, id, expected, payload]))
      .digest("hex");
    const [receipt] = await tx
      .select()
      .from(siteMutationReceipts)
      .where(
        and(
          eq(siteMutationReceipts.organizationId, by.organizationId),
          eq(siteMutationReceipts.userId, by.userId),
          eq(siteMutationReceipts.operationId, input.operationId),
          sql`studio.site_action_allowed(${siteMutationReceipts.projectId},${siteMutationReceipts.recordId},${siteMutationReceipts.action})`,
        ),
      )
      .limit(1);
    if (receipt) {
      if (receipt.requestHash !== hash)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation for different values.");
      if (action === "create") {
        const [active] = await tx
          .select({ id: siteRecords.id })
          .from(siteRecords)
          .where(
            and(
              eq(siteRecords.id, id),
              eq(siteRecords.organizationId, by.organizationId),
              eq(siteRecords.projectId, projectId),
              isNull(siteRecords.deletedAt),
            ),
          )
          .limit(1);
        if (!active) throw new AppError(404, "SITE_RECORD_NOT_FOUND", "Record no longer available.");
      }
      return siteMutationResult.parse(receipt.result);
    }
    const rows = (await tx.execute(
      sql`select studio.mutate_site_record(${projectId}::uuid,${id}::uuid,${action},${expected}::integer,${JSON.stringify(payload)}::jsonb) result`,
    )) as { rows: { result: Record<string, unknown> | null }[] };
    const raw = rows.rows[0]?.result;
    if (!raw) throw new AppError(404, "SITE_RECORD_NOT_FOUND", "Record no longer available.");
    if (raw.error) throw new AppError(409, "SITE_RECORD_ID_IN_USE", "Use a new record id.");
    const result = siteMutationResult.parse({ ...raw, operationId: input.operationId });
    const inserted = await tx
      .insert(siteMutationReceipts)
      .values({
        organizationId: by.organizationId,
        userId: by.userId,
        operationId: input.operationId,
        projectId,
        recordId: id,
        action,
        requestHash: hash,
        result,
      })
      .onConflictDoNothing()
      .returning({ id: siteMutationReceipts.operationId });
    if (!inserted.length) throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id.");
    if (result.outcome !== "conflict")
      await recordAudit(tx, {
        organizationId: by.organizationId,
        actor: by.userId,
        action: "site_record." + action,
        resourceType: "site_record",
        resourceId: id,
        requestId,
        detail: { projectId, operationId: input.operationId },
      });
    return result;
  });
}
