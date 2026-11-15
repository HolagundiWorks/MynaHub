import type { Db } from "./db/types.js";
import { events } from "./db/schema/index.js";

export interface AuditEvent {
  organizationId?: string | null;
  actor?: string | null;
  action: string;
  resourceType?: string;
  resourceId?: string;
  requestId?: string;
  /** Facts about the event. Never put a token, a password or a secret here. */
  detail?: Record<string, unknown>;
}

/** Appends one event to the append-only audit log (guidelines section 15.3). */
export async function recordAudit(db: Db, e: AuditEvent): Promise<void> {
  await db.insert(events).values({
    organizationId: e.organizationId ?? null,
    actorAuthUserId: e.actor ?? null,
    action: e.action,
    resourceType: e.resourceType ?? "",
    resourceId: e.resourceId ?? "",
    requestId: e.requestId ?? "",
    detail: e.detail ?? {},
  });
}
