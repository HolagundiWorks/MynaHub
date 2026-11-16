import { and, eq } from "drizzle-orm";
import type { Db } from "./db/types.js";
import { membershipRoles, memberships, organizations, rolePermissions, roles } from "./db/schema/index.js";
import { ROLE_TEMPLATES } from "./authz/catalogue.js";
export { PERMISSIONS, ROLE_TEMPLATES } from "./authz/catalogue.js";

/** Permission keys defined so far (resource.action). The full catalogue arrives with the authorization phase; these are what authentication needs. */
import { PERMISSIONS } from "./authz/catalogue.js";

/** Permissions that make an account an administrator: they require two-factor authentication (guidelines section 6.4). */
export const ADMIN_PERMISSIONS: ReadonlySet<string> = new Set([
  PERMISSIONS.membersManage,
  PERMISSIONS.rolesManage,
  PERMISSIONS.clientsDelete,
]);

export const OWNER_ROLE = "Owner";

/** The default roles of guidelines section 8.2, created for the organisation as system roles. */

/** This deployment's organisation. One company runs one deployment; there is at most one row. */
export async function getOrganization(db: Db): Promise<{ id: string; name: string } | null> {
  const [org] = await db
    .select({ id: organizations.id, name: organizations.name })
    .from(organizations)
    .limit(1);
  return org ?? null;
}

/** Creates the organisation and its system roles. Returns the role ids by name. */
export async function seedOrganization(
  db: Db,
  input: { name: string; slug: string },
): Promise<{ organizationId: string; roleIds: Record<string, string> }> {
  const [org] = await db
    .insert(organizations)
    .values({ name: input.name, slug: input.slug })
    .returning({ id: organizations.id });
  if (!org) throw new Error("organisation was not created");
  const roleIds: Record<string, string> = {};
  for (const t of ROLE_TEMPLATES) {
    const [role] = await db
      .insert(roles)
      .values({
        organizationId: org.id,
        name: t.name,
        description: t.description,
        isSystemRole: true,
        scope: t.scope,
      })
      .returning({ id: roles.id });
    if (!role) throw new Error("role was not created");
    roleIds[t.name] = role.id;
    await db
      .insert(rolePermissions)
      .values(t.permissions.map((permissionKey) => ({ roleId: role.id, permissionKey })));
  }
  return { organizationId: org.id, roleIds };
}

/** The permission keys a membership holds through its roles. */
export async function effectivePermissions(db: Db, membershipId: string): Promise<Set<string>> {
  const rows = await db
    .selectDistinct({ key: rolePermissions.permissionKey })
    .from(membershipRoles)
    .innerJoin(rolePermissions, eq(rolePermissions.roleId, membershipRoles.roleId))
    .where(eq(membershipRoles.membershipId, membershipId));
  return new Set(rows.map((r) => r.key));
}

/** Names of the roles a membership holds. */
export async function roleNames(db: Db, membershipId: string): Promise<string[]> {
  const rows = await db
    .select({ name: roles.name })
    .from(membershipRoles)
    .innerJoin(roles, eq(roles.id, membershipRoles.roleId))
    .where(eq(membershipRoles.membershipId, membershipId));
  return rows.map((r) => r.name);
}

export async function activeOwnerCount(db: Db, organizationId: string): Promise<number> {
  const rows = await db
    .select({ id: memberships.id })
    .from(memberships)
    .innerJoin(membershipRoles, eq(membershipRoles.membershipId, memberships.id))
    .innerJoin(roles, eq(roles.id, membershipRoles.roleId))
    .where(
      and(
        eq(memberships.organizationId, organizationId),
        eq(memberships.status, "active"),
        eq(roles.name, OWNER_ROLE),
      ),
    );
  return new Set(rows.map((r) => r.id)).size;
}
