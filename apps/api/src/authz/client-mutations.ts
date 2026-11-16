import { createHash } from "node:crypto";
import { and, eq, isNull, sql } from "drizzle-orm";
import { clients, projects, clientMutationReceipts } from "../db/schema/index.js";
import { recordAudit } from "../audit.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import { AppError } from "../errors.js";
import {
  clientMutationResult,
  clientFields,
  projectClientView,
  type ClientMutationInput,
  type ClientMutationResult,
} from "../client-contract.js";
import { withProjectContext, lockOrganization } from "./context.js";
export function getProjectClient(db: Db, by: Principal, id: string) {
  return withProjectContext(db, by, async (tx) => {
    const [row] = await tx
      .select({
        projectId: projects.id,
        clientId: projects.clientId,
        clientName: clients.name,
        version: projects.version,
      })
      .from(projects)
      .leftJoin(
        clients,
        and(
          eq(clients.id, projects.clientId),
          eq(clients.organizationId, by.organizationId),
          isNull(clients.deletedAt),
        ),
      )
      .where(
        and(
          eq(projects.id, id),
          eq(projects.organizationId, by.organizationId),
          isNull(projects.deletedAt),
          sql`studio.client_action_allowed(${id}::uuid,'link')`,
        ),
      )
      .limit(1);
    if (!row) throw new AppError(404, "PROJECT_NOT_FOUND", "No such project.");
    return projectClientView.parse(row);
  });
}
export function mutateClient(
  db: Db,
  by: Principal,
  id: string,
  input: ClientMutationInput,
  requestId: string,
  action: "create" | "edit" | "delete" | "link",
): Promise<ClientMutationResult> {
  return withProjectContext(db, by, async (tx) => {
    await lockOrganization(tx, by.organizationId);
    await tx.execute(
      sql`select pg_advisory_xact_lock(hashtextextended(${by.organizationId + ":" + by.userId + ":client:" + input.operationId},0))`,
    );
    const authority = (await tx.execute(
      sql`select studio.client_action_allowed(${id}::uuid,${action}) allowed`,
    )) as { rows: { allowed: boolean }[] };
    if (authority.rows[0]?.allowed !== true)
      throw new AppError(
        action === "create" ? 403 : 404,
        action === "create"
          ? "PERMISSION_DENIED"
          : action === "link"
            ? "PROJECT_NOT_FOUND"
            : "CLIENT_NOT_FOUND",
        "No authority for this action.",
      );
    const fields =
      action === "create" || action === "edit"
        ? clientFields.parse(input)
        : action === "link"
          ? { clientId: "clientId" in input ? input.clientId : null }
          : {};
    const expected = "expectedVersion" in input ? input.expectedVersion : null;
    const hash = createHash("sha256")
      .update(JSON.stringify([action, id, expected, fields]))
      .digest("hex");
    const [receipt] = await tx
      .select()
      .from(clientMutationReceipts)
      .where(
        and(
          eq(clientMutationReceipts.organizationId, by.organizationId),
          eq(clientMutationReceipts.userId, by.userId),
          eq(clientMutationReceipts.operationId, input.operationId),
          sql`studio.client_action_allowed(${clientMutationReceipts.targetId},${clientMutationReceipts.action})`,
        ),
      )
      .limit(1);
    if (receipt) {
      if (receipt.requestHash !== hash)
        throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation for different values.");
      if (action === "create") {
        const active = (await tx.execute(sql`select studio.client_allowed(${id}::uuid) allowed`)) as {
          rows: { allowed: boolean }[];
        };
        if (active.rows[0]?.allowed !== true)
          throw new AppError(404, "CLIENT_NOT_FOUND", "Client no longer available.");
      }
      return clientMutationResult.parse(receipt.result);
    }
    const rows = (await tx.execute(
      sql`select studio.mutate_company_client(${action},${id}::uuid,${JSON.stringify(fields)}::jsonb,${expected}::integer) result`,
    )) as { rows: { result: Record<string, unknown> | null }[] };
    const raw = rows.rows[0]?.result;
    if (!raw)
      throw new AppError(
        404,
        action === "link" ? "PROJECT_NOT_FOUND" : "CLIENT_NOT_FOUND",
        "Record no longer available.",
      );
    if (raw.error)
      throw new AppError(
        raw.error === "CLIENT_NOT_FOUND" ? 404 : 409,
        String(raw.error),
        raw.error === "CLIENT_IN_USE"
          ? "Unlink this client from active projects before deletion."
          : "The client is unavailable or its id is reserved.",
      );
    const result = clientMutationResult.parse({ ...raw, operationId: input.operationId });
    const inserted = await tx
      .insert(clientMutationReceipts)
      .values({
        organizationId: by.organizationId,
        userId: by.userId,
        operationId: input.operationId,
        targetId: id,
        action,
        requestHash: hash,
        result,
      })
      .onConflictDoNothing()
      .returning({ id: clientMutationReceipts.operationId });
    if (!inserted.length) throw new AppError(409, "OPERATION_ID_REUSED", "Use a new operation id.");
    if (result.outcome !== "conflict")
      await recordAudit(tx, {
        organizationId: by.organizationId,
        actor: by.userId,
        action: "client." + action,
        resourceType: action === "link" ? "project" : "client",
        resourceId: id,
        requestId,
        detail: { operationId: input.operationId },
      });
    return result;
  });
}
