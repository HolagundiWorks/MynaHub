import { and, eq, sql } from "drizzle-orm";
import type { Auth } from "./auth.js";
import { recordAudit } from "./audit.js";
import { membershipRoles, memberships, roles, user } from "./db/schema/index.js";
import type { Db } from "./db/types.js";
import { AppError } from "./errors.js";
import { revokeAllSessions } from "./identity.js";
import { activeOwnerCount, OWNER_ROLE, roleNames } from "./organization.js";
import type { Principal } from "./principal.js";

export async function listMembers(db: Db, organizationId: string) {
  const rows = await db
    .select({
      membershipId: memberships.id,
      userId: memberships.authUserId,
      status: memberships.status,
      name: user.name,
      email: user.email,
      role: roles.name,
    })
    .from(memberships)
    .innerJoin(user, sql`${user.id}::text = ${memberships.authUserId}`)
    .leftJoin(membershipRoles, eq(membershipRoles.membershipId, memberships.id))
    .leftJoin(roles, eq(roles.id, membershipRoles.roleId))
    .where(eq(memberships.organizationId, organizationId));
  const byId = new Map<
    string,
    { membershipId: string; userId: string; status: string; name: string; email: string; roles: string[] }
  >();
  for (const r of rows) {
    const m = byId.get(r.membershipId) ?? {
      membershipId: r.membershipId,
      userId: r.userId,
      status: r.status,
      name: r.name,
      email: r.email,
      roles: [],
    };
    if (r.role) m.roles.push(r.role);
    byId.set(r.membershipId, m);
  }
  return [...byId.values()];
}

async function target(db: Db, by: Principal, membershipId: string) {
  const [m] = await db
    .select({ id: memberships.id, userId: memberships.authUserId, status: memberships.status })
    .from(memberships)
    .where(and(eq(memberships.id, membershipId), eq(memberships.organizationId, by.organizationId)))
    .limit(1);
  if (!m) throw new AppError(404, "MEMBER_NOT_FOUND", "No such member.");
  return m;
}

/**
 * Suspends a member and ends all of their sessions at once. Safeguards: you cannot suspend yourself, only an Owner can suspend an Owner, and the
 * last active Owner can never be suspended (the company would be locked out).
 */
export async function suspendMember(
  deps: { db: Db; auth: Auth },
  by: Principal,
  membershipId: string,
  requestId: string,
) {
  const { db, auth } = deps;
  const m = await target(db, by, membershipId);
  if (m.userId === by.userId)
    throw new AppError(409, "CANNOT_SUSPEND_SELF", "You cannot suspend your own account.");
  const targetRoles = await roleNames(db, m.id);
  if (targetRoles.includes(OWNER_ROLE)) {
    if (!(await roleNames(db, by.membershipId)).includes(OWNER_ROLE)) {
      throw new AppError(403, "PERMISSION_DENIED", "Only an Owner can suspend an Owner.");
    }
    if (m.status === "active" && (await activeOwnerCount(db, by.organizationId)) <= 1) {
      throw new AppError(409, "LAST_OWNER", "The last Owner cannot be suspended.");
    }
  }
  if (m.status === "suspended") return;
  await db.transaction(async (tx) => {
    await tx
      .update(memberships)
      .set({ status: "suspended", updatedAt: new Date() })
      .where(eq(memberships.id, m.id));
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: "member.suspended",
      resourceType: "membership",
      resourceId: m.id,
      requestId,
    });
  });
  await revokeAllSessions(auth, m.userId);
}

export async function reactivateMember(db: Db, by: Principal, membershipId: string, requestId: string) {
  const m = await target(db, by, membershipId);
  if (m.status === "active") return;
  await db.transaction(async (tx) => {
    await tx
      .update(memberships)
      .set({ status: "active", updatedAt: new Date() })
      .where(eq(memberships.id, m.id));
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: "member.reactivated",
      resourceType: "membership",
      resourceId: m.id,
      requestId,
    });
  });
}
