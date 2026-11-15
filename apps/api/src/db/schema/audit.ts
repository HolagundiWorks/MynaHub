import { index, jsonb, pgSchema, text, timestamp, uuid } from "drizzle-orm/pg-core";

export const audit = pgSchema("audit");

/** Append-only security and administration events (guidelines section 15.3). A trigger in the migrations refuses UPDATE and DELETE. */
export const events = audit.table(
  "events",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    organizationId: uuid("organization_id"), // null for events before an organisation exists (the one-time bootstrap)
    occurredAt: timestamp("occurred_at", { withTimezone: true }).notNull().defaultNow(),
    actorAuthUserId: text("actor_auth_user_id"),
    action: text("action").notNull(),
    resourceType: text("resource_type").notNull().default(""),
    resourceId: text("resource_id").notNull().default(""),
    requestId: text("request_id").notNull().default(""),
    detail: jsonb("detail").notNull().default({}),
  },
  (t) => [index("audit_events_org_time_idx").on(t.organizationId, t.occurredAt)],
);
