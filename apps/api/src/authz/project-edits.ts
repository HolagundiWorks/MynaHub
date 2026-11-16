import { createHash } from "node:crypto";
import { and, eq, isNull, sql } from "drizzle-orm";
import { recordAudit } from "../audit.js";
import { projects, projectEditReceipts } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import { AppError } from "../errors.js";
import type { Principal } from "../principal.js";
import {
  projectEditResult,
  type ProjectEditInput,
  type ProjectEditResult,
} from "../project-edit-contract.js";
import { withProjectContext } from "./context.js";

/** Authorization precedes receipt replay. Receipt, update and audit commit together. */
export async function editProject(
  db: Db,
  by: Principal,
  id: string,
  input: ProjectEditInput,
  requestId: string,
): Promise<ProjectEditResult> {
  try {
    return await withProjectContext(db, by, async (tx) => {
      await tx.execute(
        sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":" + input.operationId}, 0))`,
      );
      const [current] = await tx
        .select({
          id: projects.id,
          name: projects.name,
          code: projects.code,
          status: projects.status,
          version: projects.version,
        })
        .from(projects)
        .where(
          and(
            eq(projects.id, id),
            eq(projects.organizationId, by.organizationId),
            isNull(projects.deletedAt),
            sql`studio.project_allowed(${projects.id}, 'projects.read')`,
            sql`studio.project_allowed(${projects.id}, 'projects.write')`,
          ),
        )
        .for("update")
        .limit(1);
      if (!current) throw new AppError(404, "PROJECT_NOT_FOUND", "No such editable project.");
      const hash = createHash("sha256")
        .update(JSON.stringify([id, input.expectedVersion, input.name, input.code, input.status]))
        .digest("hex");
      const [receipt] = await tx
        .select()
        .from(projectEditReceipts)
        .where(
          and(
            eq(projectEditReceipts.organizationId, by.organizationId),
            eq(projectEditReceipts.userId, by.userId),
            eq(projectEditReceipts.operationId, input.operationId),
          ),
        )
        .limit(1);
      if (receipt) {
        if (receipt.requestHash !== hash)
          throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for a different edit.");
        return projectEditResult.parse(receipt.result);
      }
      let result: ProjectEditResult;
      if (current.version !== input.expectedVersion) {
        result = projectEditResult.parse({
          outcome: "conflict",
          operationId: input.operationId,
          expectedVersion: input.expectedVersion,
          submitted: { name: input.name, code: input.code, status: input.status },
          current,
        });
      } else {
        const [updated] = await tx
          .update(projects)
          .set({
            name: input.name,
            code: input.code,
            status: input.status,
            version: current.version + 1,
            updatedAt: new Date(),
          })
          .where(
            and(
              eq(projects.id, id),
              eq(projects.organizationId, by.organizationId),
              eq(projects.version, input.expectedVersion),
            ),
          )
          .returning({
            id: projects.id,
            name: projects.name,
            code: projects.code,
            status: projects.status,
            version: projects.version,
          });
        if (!updated)
          throw new AppError(
            409,
            "PROJECT_CHANGED",
            "Project access or details changed. Reload before editing.",
          );
        result = projectEditResult.parse({
          outcome: "applied",
          operationId: input.operationId,
          project: updated,
        });
        await recordAudit(tx, {
          organizationId: by.organizationId,
          actor: by.userId,
          action: "project.updated",
          resourceType: "project",
          resourceId: id,
          requestId,
          detail: {
            operationId: input.operationId,
            previousVersion: current.version,
            version: updated.version,
          },
        });
      }
      const inserted = await tx
        .insert(projectEditReceipts)
        .values({
          organizationId: by.organizationId,
          userId: by.userId,
          operationId: input.operationId,
          projectId: id,
          requestHash: hash,
          result,
        })
        .onConflictDoNothing()
        .returning({ operationId: projectEditReceipts.operationId });
      if (!inserted.length)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for a different edit.");
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
