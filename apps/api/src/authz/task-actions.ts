import { createHash } from "node:crypto";
import { and, eq, gt, sql, isNull } from "drizzle-orm";
import { memberships, user, projects, taskActionReceipts } from "../db/schema/index.js";
import { recordAudit } from "../audit.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import { AppError } from "../errors.js";
import { taskActionResult, type TaskActionInput, type TaskActionResult } from "../task-action-contract.js";
import { withProjectContext, lockOrganization } from "./context.js";
export function taskCandidates(db: Db, by: Principal, projectId: string, after?: string) {
  return withProjectContext(db, by, async (tx) => {
    const [project] = await tx
      .select({ id: projects.id })
      .from(projects)
      .where(
        and(
          eq(projects.id, projectId),
          eq(projects.organizationId, by.organizationId),
          isNull(projects.deletedAt),
          sql`studio.project_allowed(${projectId}::uuid,'projects.read') AND studio.project_allowed(${projectId}::uuid,'tasks.assign')`,
        ),
      )
      .limit(1);
    if (!project) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
    const rows = await tx
      .select({ membershipId: memberships.id, name: user.name })
      .from(memberships)
      .innerJoin(user, sql`${user.id}::text=${memberships.authUserId}`)
      .where(
        and(
          eq(memberships.organizationId, by.organizationId),
          eq(memberships.status, "active"),
          sql`studio.task_member_eligible(${projectId}::uuid,${memberships.id})`,
          after ? gt(memberships.id, after) : undefined,
        ),
      )
      .orderBy(memberships.id)
      .limit(26);
    const members = rows.slice(0, 25);
    return { members, nextCursor: rows.length > 25 ? members.at(-1)!.membershipId : null };
  });
}
export function taskAction(
  db: Db,
  by: Principal,
  projectId: string,
  taskId: string,
  input: TaskActionInput,
  requestId: string,
  action: "assign" | "delete",
): Promise<TaskActionResult> {
  return withProjectContext(db, by, async (tx) => {
    await lockOrganization(tx, by.organizationId);
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":task-action:" + input.operationId},0))`,
    );
    const authority = (await tx.execute(
      sql`select studio.task_action_allowed(${projectId}::uuid,${taskId}::uuid,${action}) allowed`,
    )) as { rows: { allowed: boolean }[] };
    if (authority.rows[0]?.allowed !== true)
      throw new AppError(404, "TASK_NOT_FOUND", "No such task action.");
    const ids = "membershipIds" in input ? [...input.membershipIds].sort() : [];
    const hash = createHash("sha256")
      .update(JSON.stringify([action, projectId, taskId, input.expectedVersion, ids]))
      .digest("hex");
    const [receipt] = await tx
      .select()
      .from(taskActionReceipts)
      .where(
        and(
          eq(taskActionReceipts.organizationId, by.organizationId),
          eq(taskActionReceipts.userId, by.userId),
          eq(taskActionReceipts.operationId, input.operationId),
          sql`studio.task_action_allowed(${taskActionReceipts.projectId},${taskActionReceipts.taskId},${taskActionReceipts.action})`,
        ),
      )
      .limit(1);
    if (receipt) {
      if (receipt.requestHash !== hash)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation for different values.");
      return taskActionResult.parse(receipt.result);
    }
    const rows = (await tx.execute(
      sql`select studio.mutate_task_action(${projectId}::uuid,${taskId}::uuid,${action},${input.expectedVersion}::integer,${sql`ARRAY[${sql.join(
        ids.map((id) => sql`${id}::uuid`),
        sql`, `,
      )}]::uuid[]`}) result`,
    )) as { rows: { result: Record<string, unknown> | null }[] };
    const raw = rows.rows[0]?.result;
    if (!raw) throw new AppError(404, "TASK_NOT_FOUND", "Task no longer available.");
    if (raw.error)
      throw new AppError(
        400,
        "INVALID_ASSIGNEES",
        "Select active company members who can read this project.",
      );
    const result = taskActionResult.parse({ ...raw, operationId: input.operationId });
    const inserted = await tx
      .insert(taskActionReceipts)
      .values({
        organizationId: by.organizationId,
        userId: by.userId,
        operationId: input.operationId,
        projectId,
        taskId,
        action,
        requestHash: hash,
        result,
      })
      .onConflictDoNothing()
      .returning({ id: taskActionReceipts.operationId });
    if (!inserted.length) throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id.");
    if (result.outcome !== "conflict")
      await recordAudit(tx, {
        organizationId: by.organizationId,
        actor: by.userId,
        action: "task." + (action === "assign" ? "assignees_changed" : "deleted"),
        resourceType: "task",
        resourceId: taskId,
        requestId,
        detail: { projectId, operationId: input.operationId },
      });
    return result;
  });
}
