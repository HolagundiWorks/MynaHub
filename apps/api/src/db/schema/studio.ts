import { sql } from "drizzle-orm";
import {
  check,
  date,
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
import type { TaskMutationResult } from "../../task-contract.js";
import type { ProjectLifecycleResult } from "../../project-lifecycle-contract.js";

export const studio = pgSchema("studio");

export const clients = studio.table(
  "clients",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    name: text("name").notNull(),
    kind: text("kind").$type<"individual" | "company" | "architect_firm">().notNull().default("individual"),
    contactPerson: text("contact_person").notNull().default(""),
    email: text("email").notNull().default(""),
    phone: text("phone").notNull().default(""),
    address: text("address").notNull().default(""),
    correspondenceAddress: text("correspondence_address").notNull().default(""),
    preferredCommunication: text("preferred_communication").notNull().default(""),
    notes: text("notes").notNull().default(""),
    version: integer("version").notNull().default(1),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    uniqueIndex("clients_org_id_unique").on(t.organizationId, t.id),
    index("clients_org_idx").on(t.organizationId, t.id),
    check("clients_name_check", sql`length(btrim(${t.name})) between 1 and 240`),
    check("clients_kind_check", sql`${t.kind} in ('individual','company','architect_firm')`),
    check(
      "clients_fields_check",
      sql`length(${t.contactPerson})<=240 AND length(${t.email})<=254 AND length(${t.phone})<=80
    AND length(${t.address})<=2000 AND length(${t.correspondenceAddress})<=2000
    AND length(${t.preferredCommunication})<=120 AND length(${t.notes})<=4000`,
    ),
    check("clients_version_check", sql`${t.version}>=1`),
  ],
);

/** A project (guidelines section 7.3). `version` is the optimistic-concurrency counter sync checks (section 12); `deleted_at` is the tombstone. */
export const projects = studio.table(
  "projects",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    clientId: uuid("client_id"),
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
    foreignKey({
      columns: [t.organizationId, t.clientId],
      foreignColumns: [clients.organizationId, clients.id],
    }),
    uniqueIndex("projects_org_id_unique").on(t.organizationId, t.id),
    // A project code is unique within the organisation when it has one; several projects may have none yet.
    uniqueIndex("projects_org_code_unique")
      .on(t.organizationId, t.code)
      .where(sql`${t.code} <> ''`),
    check("projects_status_check", sql`${t.status} in ('active', 'on_hold', 'completed', 'archived')`),
    check("projects_version_check", sql`${t.version} >= 1`),
  ],
);

/** Cloud tasks use online mutations; local Mobile practice tasks are a separate dataset. */
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
    uniqueIndex("project_tasks_org_project_id_unique").on(t.organizationId, t.projectId, t.id),
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

/** Immutable actor-scoped receipts for online task creation and edits. */
export const taskMutationReceipts = studio.table(
  "task_mutation_receipts",
  {
    organizationId: uuid("organization_id").notNull(),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id").notNull(),
    taskId: uuid("task_id").notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<TaskMutationResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId, t.taskId],
      foreignColumns: [projectTasks.organizationId, projectTasks.projectId, projectTasks.id],
    }),
  ],
);

export const projectLifecycleReceipts = studio.table(
  "project_lifecycle_receipts",
  {
    organizationId: uuid("organization_id").notNull(),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id").notNull(),
    action: text("action").$type<"create" | "delete">().notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<ProjectLifecycleResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId],
      foreignColumns: [projects.organizationId, projects.id],
    }),
    check("project_lifecycle_action_check", sql`${t.action} in ('create','delete')`),
  ],
);

export const clientMutationReceipts = studio.table(
  "client_mutation_receipts",
  {
    organizationId: uuid("organization_id")
      .notNull()
      .references(() => organizations.id),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    targetId: uuid("target_id").notNull(),
    action: text("action").$type<"create" | "edit" | "delete" | "link">().notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<import("../../client-contract.js").ClientMutationResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    check("client_mutation_action_check", sql`${t.action} in ('create','edit','delete','link')`),
  ],
);

export const taskAssignments = studio.table(
  "task_assignments",
  {
    organizationId: uuid("organization_id").notNull(),
    projectId: uuid("project_id").notNull(),
    taskId: uuid("task_id").notNull(),
    membershipId: uuid("membership_id")
      .notNull()
      .references(() => memberships.id),
  },
  (t) => [
    primaryKey({ columns: [t.taskId, t.membershipId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId, t.taskId],
      foreignColumns: [projectTasks.organizationId, projectTasks.projectId, projectTasks.id],
    }),
  ],
);
export const taskActionReceipts = studio.table(
  "task_action_receipts",
  {
    organizationId: uuid("organization_id").notNull(),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id").notNull(),
    taskId: uuid("task_id").notNull(),
    action: text("action").$type<"assign" | "delete">().notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<import("../../task-action-contract.js").TaskActionResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId, t.taskId],
      foreignColumns: [projectTasks.organizationId, projectTasks.projectId, projectTasks.id],
    }),
    check("task_action_check", sql`${t.action} in ('assign','delete')`),
  ],
);

export const siteRecords = studio.table(
  "site_records",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id").notNull(),
    projectId: uuid("project_id").notNull(),
    kind: text("kind").$type<"instruction" | "inspection" | "snag">().notNull(),
    recordDate: date("record_date").notNull(),
    title: text("title").notNull(),
    details: text("details").notNull().default(""),
    location: text("location").notNull().default(""),
    assignedTo: text("assigned_to").notNull().default(""),
    dueDate: date("due_date"),
    status: text("status").$type<"open" | "resolved" | "closed">().notNull().default("open"),
    resolvedDate: date("resolved_date"),
    createdBy: text("created_by").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    version: integer("version").notNull().default(1),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    foreignKey({
      columns: [t.organizationId, t.projectId],
      foreignColumns: [projects.organizationId, projects.id],
    }),
    uniqueIndex("site_records_org_project_id_unique").on(t.organizationId, t.projectId, t.id),
    index("site_records_project_idx").on(t.organizationId, t.projectId, t.id),
    check(
      "site_record_fields_check",
      sql`length(btrim(${t.title})) between 1 and 240 AND length(${t.details})<=10000 AND length(${t.location})<=240 AND length(${t.assignedTo})<=240`,
    ),
    check("site_record_kind_check", sql`${t.kind} in ('instruction','inspection','snag')`),
    check("site_record_status_check", sql`${t.status} in ('open','resolved','closed')`),
    check(
      "site_record_dates_check",
      sql`${t.recordDate} between date '0001-01-01' and date '9999-12-31' AND (${t.dueDate} IS NULL OR ${t.dueDate} between date '0001-01-01' and date '9999-12-31') AND
 ((${t.status}='open' AND ${t.resolvedDate} IS NULL) OR (${t.status}<>'open' AND ${t.resolvedDate} IS NOT NULL AND ${t.resolvedDate}>=${t.recordDate} AND ${t.resolvedDate}<=date '9999-12-31'))`,
    ),
    check("site_record_version_check", sql`${t.version}>=1`),
  ],
);
export const siteMutationReceipts = studio.table(
  "site_mutation_receipts",
  {
    organizationId: uuid("organization_id").notNull(),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id").notNull(),
    recordId: uuid("record_id").notNull(),
    action: text("action").$type<"create" | "edit" | "delete">().notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<import("../../site-contract.js").SiteMutationResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId, t.recordId],
      foreignColumns: [siteRecords.organizationId, siteRecords.projectId, siteRecords.id],
    }),
    check("site_mutation_action_check", sql`${t.action} in ('create','edit','delete')`),
  ],
);

export const attachments = studio.table(
  "attachments",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id").notNull(),
    projectId: uuid("project_id").notNull(),
    name: text("name").notNull(),
    contentType: text("content_type")
      .$type<"application/pdf" | "image/png" | "image/jpeg" | "image/webp" | "text/plain" | "text/csv">()
      .notNull(),
    size: integer("size").notNull(),
    sha256: text("sha256").notNull(),
    objectKey: text("object_key").notNull().unique(),
    createdBy: text("created_by").notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
    version: integer("version").notNull().default(1),
    deletedAt: timestamp("deleted_at", { withTimezone: true }),
  },
  (t) => [
    foreignKey({
      columns: [t.organizationId, t.projectId],
      foreignColumns: [projects.organizationId, projects.id],
    }),
    uniqueIndex("attachments_org_project_id_unique").on(t.organizationId, t.projectId, t.id),
    index("attachments_project_idx").on(t.organizationId, t.projectId, t.id),
    check(
      "attachment_fields_check",
      sql`length(btrim(${t.name})) between 1 and 180 AND ${t.size} between 1 and 5242880 AND ${t.sha256} ~ '^[a-f0-9]{64}$' AND ${t.version}>=1`,
    ),
    check(
      "attachment_type_check",
      sql`${t.contentType} in ('application/pdf','image/png','image/jpeg','image/webp','text/plain','text/csv')`,
    ),
  ],
);
export const attachmentReceipts = studio.table(
  "attachment_receipts",
  {
    organizationId: uuid("organization_id").notNull(),
    userId: text("user_id").notNull(),
    operationId: uuid("operation_id").notNull(),
    projectId: uuid("project_id").notNull(),
    fileId: uuid("file_id").notNull(),
    action: text("action").$type<"upload" | "delete">().notNull(),
    requestHash: text("request_hash").notNull(),
    result: jsonb("result").$type<import("../../attachment-contract.js").AttachmentResult>().notNull(),
    createdAt: timestamp("created_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    primaryKey({ columns: [t.organizationId, t.userId, t.operationId] }),
    foreignKey({
      columns: [t.organizationId, t.projectId, t.fileId],
      foreignColumns: [attachments.organizationId, attachments.projectId, attachments.id],
    }),
    check("attachment_action_check", sql`${t.action} in ('upload','delete')`),
  ],
);
