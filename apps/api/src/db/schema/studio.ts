import { sql } from "drizzle-orm";
import { check, index, integer, pgSchema, text, timestamp, uniqueIndex, uuid } from "drizzle-orm/pg-core";
import { organizations } from "./core.js";

export const studio = pgSchema("studio");

/** A project (guidelines section 7.3). `version` is the optimistic-concurrency counter sync checks (section 12); `deleted_at` is the tombstone. */
export const projects = studio.table(
  "projects",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    name: text("name").notNull(),
    code: text("code").notNull().default(""),
    status: text("status").notNull().default("active"),
    createdBy: text("created_by").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
    version: integer("version").notNull().default(1),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    index("projects_org_idx").on(t.organizationId),
    // A project code is unique within the organisation when it has one; several projects may have none yet.
    uniqueIndex("projects_org_code_unique")
      .on(t.organizationId, t.code)
      .where(sql`${t.code} <> ''`),
    check("projects_status_check", sql`${t.status} in ('active', 'on_hold', 'completed', 'archived')`),
    check("projects_version_check", sql`${t.version} >= 1`),
  ],
);
