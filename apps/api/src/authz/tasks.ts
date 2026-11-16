import { and, eq, gt, isNull, sql } from "drizzle-orm";
import { projectTasks } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import { withProjectContext } from "./context.js";

/** Same project scope as project details. No mutation or local-practice import surface. */
export function listProjectTasks(db: Db, by: Principal, projectId: string, after?: string) {
  return withProjectContext(db, by, async (tx) => {
    const rows = await tx
      .select({
        id: projectTasks.id,
        projectId: projectTasks.projectId,
        title: projectTasks.title,
        description: projectTasks.description,
        dueAt: projectTasks.dueAt,
        status: projectTasks.status,
        priority: projectTasks.priority,
        version: projectTasks.version,
      })
      .from(projectTasks)
      .where(
        and(
          eq(projectTasks.organizationId, by.organizationId),
          eq(projectTasks.projectId, projectId),
          isNull(projectTasks.deletedAt),
          sql`studio.project_allowed(${projectTasks.projectId}, 'projects.read')`,
          after ? gt(projectTasks.id, after) : undefined,
        ),
      )
      .orderBy(projectTasks.id)
      .limit(26);
    const page = rows.slice(0, 25).map((row) => ({ ...row, dueAt: row.dueAt?.toISOString() ?? null }));
    return { projectId, tasks: page, nextCursor: rows.length > 25 ? page.at(-1)!.id : null };
  });
}
