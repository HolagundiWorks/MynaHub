import { and, eq, inArray } from "drizzle-orm";
import { recordAudit } from "../audit.js";
import { memberships, membershipRoles, roles, rolePermissions } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import { AppError } from "../errors.js";
import { activeOwnerCount, effectivePermissions, roleNames } from "../organization.js";
import type { Principal } from "../principal.js";
import { PERMISSIONS, PERMISSION_CATALOGUE, type RoleScope } from "./catalogue.js";
import { lockOrganization } from "./context.js";

export interface RoleInput {
  name: string;
  description: string;
  scope: RoleScope;
  permissions: string[];
}
const denied = () =>
  new AppError(403, "PERMISSION_DENIED", "You cannot grant permissions or ownership beyond your authority.");

async function authority(db: Db, by: Principal) {
  const [member] = await db
    .select()
    .from(memberships)
    .where(
      and(
        eq(memberships.id, by.membershipId),
        eq(memberships.organizationId, by.organizationId),
        eq(memberships.authUserId, by.userId),
      ),
    )
    .limit(1);
  if (!member || member.status !== "active") throw denied();
  const granted = await effectivePermissions(db, by.membershipId);
  const admin = await db
    .select({ scope: roles.scope })
    .from(roles)
    .innerJoin(membershipRoles, eq(membershipRoles.roleId, roles.id))
    .innerJoin(rolePermissions, eq(rolePermissions.roleId, roles.id))
    .where(
      and(
        eq(membershipRoles.membershipId, by.membershipId),
        eq(roles.organizationId, by.organizationId),
        eq(rolePermissions.permissionKey, PERMISSIONS.rolesManage),
        eq(roles.scope, "organization"),
      ),
    );
  if (!admin.length) throw denied();
  return {
    granted,
    organizationPermissions: await organizationPermissions(db, by),
    owner: (await roleNames(db, by.membershipId)).includes("Owner"),
  };
}

async function organizationPermissions(db: Db, by: Principal): Promise<Set<string>> {
  const rows = await db
    .select({ key: rolePermissions.permissionKey })
    .from(rolePermissions)
    .innerJoin(roles, eq(roles.id, rolePermissions.roleId))
    .innerJoin(membershipRoles, eq(membershipRoles.roleId, roles.id))
    .innerJoin(memberships, eq(memberships.id, membershipRoles.membershipId))
    .where(
      and(
        eq(memberships.id, by.membershipId),
        eq(memberships.authUserId, by.userId),
        eq(memberships.status, "active"),
        eq(memberships.organizationId, by.organizationId),
        eq(roles.organizationId, by.organizationId),
        eq(roles.scope, "organization"),
      ),
    );
  return new Set(rows.map((row) => row.key));
}

function validateDelegation(
  granted: Set<string>,
  input: Pick<RoleInput, "scope" | "permissions">,
  organizationGranted: Set<string>,
) {
  const known = new Set(PERMISSION_CATALOGUE.map((p) => p.key));
  if (input.permissions.some((key) => !known.has(key as (typeof PERMISSION_CATALOGUE)[number]["key"])))
    throw new AppError(400, "UNKNOWN_PERMISSION", "The role contains an unknown permission.");
  if (input.permissions.some((key) => !granted.has(key))) throw denied();
  if (input.scope === "organization" && input.permissions.some((key) => !organizationGranted.has(key)))
    throw denied();
  if (
    input.scope === "assigned" &&
    input.permissions.some((key) => key === PERMISSIONS.rolesManage || key === PERMISSIONS.membersManage)
  )
    throw new AppError(400, "INVALID_SCOPE", "Company administration requires organization scope.");
  if (
    (input.permissions.includes(PERMISSIONS.projectsAssign) ||
      input.permissions.includes(PERMISSIONS.projectsWrite)) &&
    !input.permissions.includes(PERMISSIONS.projectsRead)
  )
    throw new AppError(
      400,
      "INVALID_PERMISSIONS",
      "Project assignment and editing need project read permission.",
    );
}

export async function listRoles(db: Db, organizationId: string) {
  const rows = await db
    .select()
    .from(roles)
    .where(eq(roles.organizationId, organizationId))
    .orderBy(roles.name)
    .limit(100);
  const keys = rows.length
    ? await db
        .select()
        .from(rolePermissions)
        .where(
          inArray(
            rolePermissions.roleId,
            rows.map((row) => row.id),
          ),
        )
    : [];
  const result = [];
  for (const row of rows) {
    result.push({
      ...row,
      permissions: keys
        .filter((p) => p.roleId === row.id)
        .map((p) => p.permissionKey)
        .sort(),
    });
  }
  return result;
}

/** Invitation-based role assignment must obey the same delegation boundary as direct assignment. */
export async function assertInvitationRole(db: Db, by: Principal, roleId: string) {
  const [role] = await db
    .select()
    .from(roles)
    .where(and(eq(roles.id, roleId), eq(roles.organizationId, by.organizationId)))
    .limit(1);
  if (!role) throw new AppError(404, "ROLE_NOT_FOUND", "That role does not exist.");
  const keys = await db
    .select({ key: rolePermissions.permissionKey })
    .from(rolePermissions)
    .where(eq(rolePermissions.roleId, role.id));
  validateDelegation(
    await effectivePermissions(db, by.membershipId),
    {
      scope: role.scope,
      permissions: keys.map((k) => k.key),
    },
    await organizationPermissions(db, by),
  );
  return role;
}

function roleError(err: unknown): never {
  if (
    (err as { cause?: { code?: string }; code?: string }).cause?.code === "23505" ||
    (err as { code?: string }).code === "23505"
  )
    throw new AppError(409, "ROLE_EXISTS", "A role with that name already exists.");
  throw err;
}

export async function saveRole(db: Db, by: Principal, input: RoleInput, requestId: string, roleId?: string) {
  try {
    return await db.transaction(async (tx) => {
      await lockOrganization(tx, by.organizationId);
      const actor = await authority(tx, by);
      validateDelegation(actor.granted, input, actor.organizationPermissions);
      const permissionKeys = [...new Set(input.permissions)].sort();
      let id: string;
      if (roleId) {
        const [existing] = await tx
          .select()
          .from(roles)
          .where(and(eq(roles.id, roleId), eq(roles.organizationId, by.organizationId)))
          .limit(1);
        if (!existing) throw new AppError(404, "ROLE_NOT_FOUND", "No such role.");
        if (existing.isSystemRole)
          throw new AppError(409, "SYSTEM_ROLE", "System roles cannot be changed. Create a custom role.");
        await tx
          .update(roles)
          .set({ name: input.name, description: input.description, scope: input.scope })
          .where(eq(roles.id, roleId));
        await tx.delete(rolePermissions).where(eq(rolePermissions.roleId, roleId));
        id = roleId;
      } else {
        if (
          (
            await tx
              .select({ id: roles.id })
              .from(roles)
              .where(eq(roles.organizationId, by.organizationId))
              .limit(100)
          ).length >= 100
        )
          throw new AppError(409, "ROLE_LIMIT", "This company has reached its role limit.");
        const [created] = await tx
          .insert(roles)
          .values({
            organizationId: by.organizationId,
            name: input.name,
            description: input.description,
            scope: input.scope,
            isSystemRole: false,
          })
          .returning({ id: roles.id });
        if (!created) throw new Error("Role insert failed");
        id = created.id;
      }
      if (permissionKeys.length)
        await tx
          .insert(rolePermissions)
          .values(permissionKeys.map((permissionKey) => ({ roleId: id, permissionKey })));
      await recordAudit(tx, {
        organizationId: by.organizationId,
        actor: by.userId,
        action: roleId ? "role.updated" : "role.created",
        resourceType: "role",
        resourceId: id,
        requestId,
        detail: { name: input.name, scope: input.scope, permissions: permissionKeys },
      });
      return { id };
    });
  } catch (err) {
    roleError(err);
  }
}

export async function setMemberRoles(
  db: Db,
  by: Principal,
  membershipId: string,
  roleIds: string[],
  requestId: string,
) {
  return db.transaction(async (tx) => {
    await lockOrganization(tx, by.organizationId);
    const actor = await authority(tx, by);
    const [member] = await tx
      .select()
      .from(memberships)
      .where(and(eq(memberships.id, membershipId), eq(memberships.organizationId, by.organizationId)))
      .limit(1);
    if (!member) throw new AppError(404, "MEMBER_NOT_FOUND", "No such member.");
    const ids = [...new Set(roleIds)];
    const chosen = await tx
      .select()
      .from(roles)
      .where(and(eq(roles.organizationId, by.organizationId), inArray(roles.id, ids)));
    if (chosen.length !== ids.length) throw new AppError(404, "ROLE_NOT_FOUND", "No such role.");
    const before = await roleNames(tx, membershipId);
    const remainsOwner = chosen.some((r) => r.name === "Owner");
    if (!actor.owner && (before.includes("Owner") || remainsOwner)) throw denied();
    for (const role of chosen) {
      const keys = await tx
        .select({ key: rolePermissions.permissionKey })
        .from(rolePermissions)
        .where(eq(rolePermissions.roleId, role.id));
      validateDelegation(
        actor.granted,
        {
          scope: role.scope as RoleScope,
          permissions: keys.map((k) => k.key),
        },
        actor.organizationPermissions,
      );
    }
    if (
      before.includes("Owner") &&
      !remainsOwner &&
      member.status === "active" &&
      (await activeOwnerCount(tx, by.organizationId)) <= 1
    )
      throw new AppError(409, "LAST_OWNER", "The last active Owner cannot be demoted.");
    await tx.delete(membershipRoles).where(eq(membershipRoles.membershipId, membershipId));
    await tx.insert(membershipRoles).values(ids.map((roleId) => ({ membershipId, roleId })));
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: "member.roles_changed",
      resourceType: "membership",
      resourceId: membershipId,
      requestId,
      detail: { roleIds: ids },
    });
  });
}
