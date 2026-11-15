import { sql } from "drizzle-orm";
import { check, index, pgSchema, text, timestamp, unique, uuid } from "drizzle-orm/pg-core";

export const core = pgSchema("core");

/** One company's Myna deployment is one organisation (guidelines section 7.3). Every company-owned row below carries its organisation id. */
export const organizations = core.table("organizations", {
  id: uuid("id").primaryKey().defaultRandom(),
  name: text("name").notNull(),
  slug: text("slug").notNull().unique(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
});

/** A person's membership of an organisation. `auth_user_id` is the Better Auth user id (Phase 2); no foreign key into the auth schema, which the auth library owns. */
export const memberships = core.table(
  "memberships",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    authUserId: text("auth_user_id").notNull(),
    status: text("status").notNull().default("invited"),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    unique("memberships_org_user_unique").on(t.organizationId, t.authUserId),
    index("memberships_user_idx").on(t.authUserId),
    check("memberships_status_check", sql`${t.status} in ('invited', 'active', 'suspended')`),
  ],
);
