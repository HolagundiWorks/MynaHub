import { and, eq, gt, isNull, sql } from "drizzle-orm";
import { recordAudit } from "../audit.js";
import { memberships, projectAssignments, projects } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import { AppError } from "../errors.js";
import type { Principal } from "../principal.js";
import { withProjectContext } from "./context.js";

const fields = {
  id: projects.id,
  name: projects.name,
  code: projects.code,
  status: projects.status,
  version: projects.version,
};
const visible = (by: Principal) =>
  and(
    eq(projects.organizationId, by.organizationId),
    isNull(projects.deletedAt),
    sql`studio.project_allowed(${projects.id}, 'projects.read')`,
  );

export function listProjects(db: Db, by: Principal, limit: number, after?: string) {
  return withProjectContext(db, by, async (tx) => {
    const rows = await tx
      .select(fields)
      .from(projects)
      .where(and(visible(by), after ? gt(projects.id, after) : undefined))
      .orderBy(projects.id)
      .limit(limit + 1);
    const more = rows.length > limit;
    const page = rows.slice(0, limit);
    return { projects: page, nextCursor: more ? (page.at(-1)?.id ?? null) : null };
  });
}

export function getProject(db: Db, by: Principal, id: string) {
  return withProjectContext(db, by, async (tx) => {
    const [row] = await tx
      .select(fields)
      .from(projects)
      .where(and(visible(by), eq(projects.id, id)))
      .limit(1);
    if (!row) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
    return row;
  });
}

export function setProjectAssignment(
  db: Db,
  by: Principal,
  id: string,
  membershipId: string,
  remove: boolean,
  requestId: string,
) {
  return withProjectContext(db, by, async (tx) => {
    const [project] = await tx
      .select({ id: projects.id })
      .from(projects)
      .where(
        and(visible(by), eq(projects.id, id), sql`studio.project_allowed(${projects.id}, 'projects.assign')`),
      )
      .limit(1);
    if (!project) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
    const [member] = await tx
      .select({ id: memberships.id })
      .from(memberships)
      .where(
        and(
          eq(memberships.id, membershipId),
          eq(memberships.organizationId, by.organizationId),
          eq(memberships.status, "active"),
        ),
      )
      .limit(1);
    if (!member) throw new AppError(404, "MEMBER_NOT_FOUND", "No active member with that id.");
    if (remove)
      await tx
        .delete(projectAssignments)
        .where(
          and(
            eq(projectAssignments.projectId, id),
            eq(projectAssignments.membershipId, membershipId),
            eq(projectAssignments.organizationId, by.organizationId),
          ),
        );
    else
      await tx
        .insert(projectAssignments)
        .values({ organizationId: by.organizationId, projectId: id, membershipId })
        .onConflictDoNothing();
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: remove ? "project.assignment_removed" : "project.assignment_added",
      resourceType: "project",
      resourceId: id,
      requestId,
      detail: { membershipId },
    });
  });
}
