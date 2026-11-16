import { createHash } from "node:crypto";
import { and, eq, sql } from "drizzle-orm";
import { recordAudit } from "../audit.js";
import { projectLifecycleReceipts } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import { AppError } from "../errors.js";
import type { Principal } from "../principal.js";
import {
  projectLifecycleResult,
  type ProjectCreateInput,
  type ProjectDeleteInput,
  type ProjectLifecycleResult,
} from "../project-lifecycle-contract.js";
import { withProjectContext } from "./context.js";

export async function projectLifecycle(
  db: Db,
  by: Principal,
  id: string,
  input: ProjectCreateInput | ProjectDeleteInput,
  requestId: string,
  action: "create" | "delete",
): Promise<ProjectLifecycleResult> {
  try {
    return await withProjectContext(db, by, async (tx) => {
      await tx.execute(
        sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":project-lifecycle:" + input.operationId},0))`,
      );
      const authority = (await tx.execute(
        action === "create"
          ? sql`select studio.organization_permission('projects.create') AND studio.organization_permission('projects.read') allowed`
          : sql`select studio.project_delete_allowed(${id}::uuid) allowed`,
      )) as { rows: { allowed: boolean }[] };
      if (authority.rows[0]?.allowed !== true)
        throw new AppError(
          action === "create" ? 403 : 404,
          action === "create" ? "PERMISSION_DENIED" : "PROJECT_NOT_FOUND",
          "No authority for this project action.",
        );
      const payload =
        action === "create"
          ? [
              (input as ProjectCreateInput).name,
              (input as ProjectCreateInput).code,
              (input as ProjectCreateInput).status,
            ]
          : [(input as ProjectDeleteInput).expectedVersion, (input as ProjectDeleteInput).confirmation];
      const hash = createHash("sha256")
        .update(JSON.stringify([action, id, payload]))
        .digest("hex");
      const [receipt] = await tx
        .select()
        .from(projectLifecycleReceipts)
        .where(
          and(
            eq(projectLifecycleReceipts.organizationId, by.organizationId),
            eq(projectLifecycleReceipts.userId, by.userId),
            eq(projectLifecycleReceipts.operationId, input.operationId),
            sql`((${projectLifecycleReceipts.action}='create' AND studio.organization_permission('projects.create')
          AND studio.organization_permission('projects.read') AND studio.project_allowed(${projectLifecycleReceipts.projectId},'projects.read'))
          OR (${projectLifecycleReceipts.action}='delete' AND studio.project_delete_allowed(${projectLifecycleReceipts.projectId})))`,
          ),
        )
        .limit(1);
      if (receipt) {
        if (receipt.requestHash !== hash)
          throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for different values.");
        return projectLifecycleResult.parse(receipt.result);
      }
      let result: ProjectLifecycleResult;
      if (action === "create") {
        const fields = input as ProjectCreateInput;
        const rows = (await tx.execute(
          sql`select * from studio.create_company_project(${id}::uuid,${fields.name},${fields.code},${fields.status})`,
        )) as { rows: Record<string, unknown>[] };
        if (!rows.rows.length) throw new AppError(409, "PROJECT_ID_IN_USE", "Use a new project id.");
        result = projectLifecycleResult.parse({
          outcome: "created",
          operationId: input.operationId,
          project: rows.rows[0],
        });
      } else {
        const fields = input as ProjectDeleteInput;
        const rows = (await tx.execute(
          sql`select * from studio.delete_company_project(${id}::uuid,${fields.expectedVersion})`,
        )) as { rows: Record<string, unknown>[] };
        const row = rows.rows[0];
        if (!row) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
        result =
          row.outcome === "conflict"
            ? projectLifecycleResult.parse({
                outcome: "conflict",
                operationId: input.operationId,
                expectedVersion: fields.expectedVersion,
                current: row,
              })
            : projectLifecycleResult.parse({
                outcome: "deleted",
                operationId: input.operationId,
                id: row.id,
                version: row.version,
                deletedAt: new Date(row.deleted_at as string | Date).toISOString(),
              });
      }
      const inserted = await tx
        .insert(projectLifecycleReceipts)
        .values({
          organizationId: by.organizationId,
          userId: by.userId,
          operationId: input.operationId,
          projectId: id,
          action,
          requestHash: hash,
          result,
        })
        .onConflictDoNothing()
        .returning({ operationId: projectLifecycleReceipts.operationId });
      if (!inserted.length)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for different values.");
      if (result.outcome !== "conflict")
        await recordAudit(tx, {
          organizationId: by.organizationId,
          actor: by.userId,
          action: action === "create" ? "project.created" : "project.deleted",
          resourceType: "project",
          resourceId: id,
          requestId,
          detail: {
            operationId: input.operationId,
            version: result.outcome === "created" ? result.project.version : result.version,
          },
        });
      return result;
    });
  } catch (error) {
    const cause = error instanceof Error && "cause" in error ? error.cause : error;
    if (
      cause &&
      typeof cause === "object" &&
      "code" in cause &&
      cause.code === "23505" &&
      "constraint" in cause &&
      cause.constraint === "projects_org_code_unique"
    )
      throw new AppError(409, "PROJECT_CODE_IN_USE", "That project code is already in use.");
    throw error;
  }
}
