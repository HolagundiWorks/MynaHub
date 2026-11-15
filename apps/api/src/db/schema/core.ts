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

/**
 * The one-time setup token that creates the first administrator (guidelines section 5.3). Only a hash is stored. A token is valid once, for a short
 * time, and only while no organisation exists.
 */
export const bootstrapTokens = core.table("bootstrap_tokens", {
  id: uuid("id").primaryKey().defaultRandom(),
  tokenHash: text("token_hash").notNull().unique(),
  createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  expiresAt: timestamp("expires_at", { withTimezone: true }).notNull(),
  usedAt: timestamp("used_at", { withTimezone: true }),
});

/** An employee invitation (section 6.3): a random single-use token stored only as a hash, bound to one email and one role, expiring, revocable. */
export const invitations = core.table(
  "invitations",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    email: text("email").notNull(),
    roleId: uuid("role_id").notNull(),
    tokenHash: text("token_hash").notNull().unique(),
    status: text("status").notNull().default("pending"),
    createdBy: text("created_by").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    expiresAt: timestamp("expires_at", { withTimezone: true }).notNull(),
    acceptedAt: timestamp("accepted_at", { withTimezone: true }),
    acceptedUserId: text("accepted_user_id"),
    revokedAt: timestamp("revoked_at", { withTimezone: true }),
  },
  (t) => [
    index("invitations_org_status_idx").on(t.organizationId, t.status),
    index("invitations_email_idx").on(t.email),
    check("invitations_status_check", sql`${t.status} in ('pending', 'accepted', 'revoked')`),
  ],
);
