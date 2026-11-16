import { boolean, index, pgSchema, primaryKey, text, uuid, uniqueIndex, check } from "drizzle-orm/pg-core";
import { sql } from "drizzle-orm";
import { memberships, organizations } from "./core.js";

export const access = pgSchema("access");

/** The permission catalogue: identifiers like `tasks.update` (resource.action), shared by Hub, Mobile and the API (guidelines section 8.3). Filled in Phase 3. */
export const permissions = access.table("permissions", {
  key: text("key").primaryKey(),
  description: text("description").notNull().default(""),
  resourceType: text("resource_type").notNull(),
});

export const roles = access.table(
  "roles",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    name: text("name").notNull(),
    description: text("description").notNull().default(""),
    isSystemRole: boolean("is_system_role").notNull().default(false),
    scope: text("scope").$type<"organization" | "assigned">().notNull().default("assigned"),
  },
  (t) => [
    index("roles_org_name_idx").on(t.organizationId, t.name),
    uniqueIndex("roles_org_name_unique").on(t.organizationId, t.name),
    check("roles_scope_check", sql`${t.scope} in ('organization', 'assigned')`),
  ],
);

export const rolePermissions = access.table(
  "role_permissions",
  {
    roleId: uuid("role_id")
      .notNull()
      .references(() => roles.id, { onDelete: "cascade" }),
    permissionKey: text("permission_key")
      .notNull()
      .references(() => permissions.key),
  },
  (t) => [primaryKey({ columns: [t.roleId, t.permissionKey] })],
);

export const membershipRoles = access.table(
  "membership_roles",
  {
    membershipId: uuid("membership_id")
      .notNull()
      .references(() => memberships.id, { onDelete: "cascade" }),
    roleId: uuid("role_id")
      .notNull()
      .references(() => roles.id, { onDelete: "cascade" }),
  },
  (t) => [primaryKey({ columns: [t.membershipId, t.roleId] })],
);
