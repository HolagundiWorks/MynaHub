import { sql } from "drizzle-orm";
import {
  check,
  index,
  integer,
  pgSchema,
  text,
  timestamp,
  uniqueIndex,
  uuid,
  primaryKey,
  jsonb,
  foreignKey,
} from "drizzle-orm/pg-core";
import { organizations, memberships } from "./core.js";
import type { ProjectEditResult } from "../../project-edit-contract.js";

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
    status: text("status")
      .$type<"active" | "on_hold" | "completed" | "archived">()
      .notNull()
      .default("active"),
    createdBy: text("created_by").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
    version: integer("version").notNull().default(1),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    index("projects_org_idx").on(t.organizationId),
    uniqueIndex("projects_org_id_unique").on(t.organizationId, t.id),
    // A project code is unique within the organisation when it has one; several projects may have none yet.
    uniqueIndex("projects_org_code_unique")
      .on(t.organizationId, t.code)
      .where(sql`${t.code} <> ''`),
    check("projects_status_check", sql`${t.status} in ('active', 'on_hold', 'completed', 'archived')`),
    check("projects_version_check", sql`${t.version} >= 1`),
  ],
);

/** Cloud tasks are read online; local Mobile practice tasks are a separate dataset. */
export const projectTasks = studio.table(
  "project_tasks",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id").notNull(),
    projectId: uuid("project_id").notNull(),
    title: text("title").notNull(),
    description: text("description").notNull().default(""),
    dueAt: timestamp("due_at", { withTimezone: true }),
    status: text("status")
      .$type<"open" | "in_progress" | "completed" | "cancelled">()
      .notNull()
      .default("open"),
    priority: text("priority").$type<"low" | "normal" | "high" | "urgent">().notNull().default("normal"),
    version: integer("version").notNull().default(1),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    foreignKey({
      columns: [t.organizationId, t.projectId],
      foreignColumns: [projects.organizationId, projects.id],
    }),
    index("project_tasks_project_idx").on(t.organizationId, t.projectId, t.id),
    check("project_tasks_title_check", sql`length(btrim(${t.title})) between 1 and 240`),
    check("project_tasks_description_check", sql`length(${t.description}) <= 10000`),
    check("project_tasks_status_check", sql`${t.status} in ('open','in_progress','completed','cancelled')`),
    check("project_tasks_priority_check", sql`${t.priority} in ('low','normal','high','urgent')`),
    check("project_tasks_version_check", sql`${t.version} >= 1`),
  ],
);

/** Explicit assignment, kept in the project schema so row security can scope field staff. */
export const projectAssignments = studio.table(
  "project_assignments",
  {
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    membershipId: uuid("membership_id")
      .notNull()
      .references(() => memberships.id),
  },
  (t) => [
    primaryKey({ columns: [t.projectId, t.membershipId] }),
    index("project_assignments_member_idx").on(t.membershipId),
  ],
);

/** Immutable, actor-scoped receipts for explicit online project edits. */
export const projectEditReceipts = studio.table(
  "project_edit_receipts",
  {
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id")
      .notNull()
      .references(() => projects.id),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<ProjectEditResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [primaryKey({ columns: [t.organizationId, t.userId, t.operationId] })],
);
