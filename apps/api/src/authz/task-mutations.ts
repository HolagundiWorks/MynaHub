import { createHash } from "node:crypto";
import { and, eq, isNull, sql } from "drizzle-orm";
import { recordAudit } from "../audit.js";
import { projects, projectTasks, taskMutationReceipts } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import { AppError } from "../errors.js";
import type { Principal } from "../principal.js";
import {
  taskMutationResult,
  taskView,
  type TaskCreateInput,
  type TaskEditInput,
  type TaskMutationResult,
} from "../task-contract.js";
import { withProjectContext } from "./context.js";

const fields = {
  id: projectTasks.id,
  projectId: projectTasks.projectId,
  title: projectTasks.title,
  description: projectTasks.description,
  dueAt: projectTasks.dueAt,
  status: projectTasks.status,
  priority: projectTasks.priority,
  version: projectTasks.version,
  assignees: sql<{ membershipId: string; name: string }[]>`studio.task_assignees(${projectTasks.id})`,
};
const view = (row: { dueAt: Date | null }) =>
  taskView.parse({ ...row, dueAt: row.dueAt?.toISOString() ?? null });

/** Current authority is checked before replay. Task, receipt and audit commit atomically. */
export function mutateTask(
  db: Db,
  by: Principal,
  projectId: string,
  taskId: string,
  input: TaskCreateInput | TaskEditInput,
  requestId: string,
  create: boolean,
): Promise<TaskMutationResult> {
  return withProjectContext(db, by, async (tx) => {
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":task:" + input.operationId},0))`,
    );
    const [project] = await tx
      .select({ id: projects.id })
      .from(projects)
      .where(
        and(
          eq(projects.id, projectId),
          eq(projects.organizationId, by.organizationId),
          isNull(projects.deletedAt),
          sql`studio.project_allowed(${projects.id},'projects.read')`,
          sql`studio.project_allowed(${projects.id},'tasks.write')`,
        ),
      )
      .for("share")
      .limit(1);
    if (!project) throw new AppError(404, "PROJECT_NOT_FOUND", "No such editable project.");
    const submitted = {
      title: input.title,
      description: input.description,
      dueAt: input.dueAt ? new Date(input.dueAt).toISOString() : null,
      status: input.status,
      priority: input.priority,
    };
    const expectedVersion = "expectedVersion" in input ? input.expectedVersion : null;
    const hash = createHash("sha256")
      .update(JSON.stringify([create, projectId, taskId, expectedVersion, submitted]))
      .digest("hex");
    const [current] = create
      ? []
      : await tx
          .select(fields)
          .from(projectTasks)
          .where(
            and(
              eq(projectTasks.id, taskId),
              eq(projectTasks.projectId, projectId),
              eq(projectTasks.organizationId, by.organizationId),
              isNull(projectTasks.deletedAt),
            ),
          )
          .for("update")
          .limit(1);
    if (!create && !current) throw new AppError(404, "TASK_NOT_FOUND", "No such editable task.");
    const [receipt] = await tx
      .select()
      .from(taskMutationReceipts)
      .where(
        and(
          eq(taskMutationReceipts.organizationId, by.organizationId),
          eq(taskMutationReceipts.userId, by.userId),
          eq(taskMutationReceipts.operationId, input.operationId),
        ),
      )
      .limit(1);
    if (receipt) {
      if (receipt.requestHash !== hash)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for different values.");
      if (create) {
        const active = await tx
          .select({ id: projectTasks.id })
          .from(projectTasks)
          .where(
            and(
              eq(projectTasks.id, taskId),
              eq(projectTasks.projectId, projectId),
              eq(projectTasks.organizationId, by.organizationId),
              isNull(projectTasks.deletedAt),
            ),
          )
          .limit(1);
        if (!active.length) throw new AppError(404, "TASK_NOT_FOUND", "Task is no longer available.");
      }
      return taskMutationResult.parse(receipt.result);
    }
    let result: TaskMutationResult;
    if (current && current.version !== expectedVersion) {
      result = taskMutationResult.parse({
        outcome: "conflict",
        operationId: input.operationId,
        expectedVersion,
        submitted,
        current: view(current),
      });
    } else {
      const values = { ...submitted, dueAt: input.dueAt ? new Date(input.dueAt) : null };
      const [saved] = create
        ? await tx
            .insert(projectTasks)
            .values({ ...values, id: taskId, organizationId: by.organizationId, projectId })
            .onConflictDoNothing()
            .returning(fields)
        : await tx
            .update(projectTasks)
            .set({ ...values, version: current!.version + 1, updatedAt: new Date() })
            .where(
              and(
                eq(projectTasks.id, taskId),
                eq(projectTasks.organizationId, by.organizationId),
                eq(projectTasks.projectId, projectId),
                eq(projectTasks.version, expectedVersion!),
                sql`studio.project_allowed(${projectTasks.projectId},'projects.read')`,
                sql`studio.project_allowed(${projectTasks.projectId},'tasks.write')`,
              ),
            )
            .returning(fields);
      if (!saved)
        throw new AppError(
          409,
          create ? "TASK_ID_IN_USE" : "TASK_CHANGED",
          create ? "Use a new task id." : "Task access or details changed. Reload before editing.",
        );
      result = taskMutationResult.parse({
        outcome: "applied",
        operationId: input.operationId,
        task: view(saved),
      });
      await recordAudit(tx, {
        organizationId: by.organizationId,
        actor: by.userId,
        action: create ? "task.created" : "task.updated",
        resourceType: "task",
        resourceId: taskId,
        requestId,
        detail: {
          projectId,
          operationId: input.operationId,
          previousVersion: expectedVersion,
          version: saved.version,
        },
      });
    }
    const inserted = await tx
      .insert(taskMutationReceipts)
      .values({
        organizationId: by.organizationId,
        userId: by.userId,
        operationId: input.operationId,
        projectId,
        taskId,
        requestHash: hash,
        result,
      })
      .onConflictDoNothing()
      .returning({ operationId: taskMutationReceipts.operationId });
    if (!inserted.length)
      throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id for different values.");
    return result;
  });
}
