import { and, eq, gt, isNull, sql } from "drizzle-orm";
import { clients } from "../db/schema/index.js";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";
import { AppError } from "../errors.js";
import { withProjectContext } from "./context.js";
const fields = {
  id: clients.id,
  name: clients.name,
  kind: clients.kind,
  contactPerson: clients.contactPerson,
  email: clients.email,
  phone: clients.phone,
  address: clients.address,
  correspondenceAddress: clients.correspondenceAddress,
  preferredCommunication: clients.preferredCommunication,
  notes: clients.notes,
  version: clients.version,
};
const visible = (by: Principal) =>
  and(
    eq(clients.organizationId, by.organizationId),
    isNull(clients.deletedAt),
    sql`studio.client_allowed(${clients.id})`,
  );
export function listClients(db: Db, by: Principal, after?: string) {
  return withProjectContext(db, by, async (tx) => {
    const rows = await tx
      .select(fields)
      .from(clients)
      .where(and(visible(by), after ? gt(clients.id, after) : undefined))
      .orderBy(clients.id)
      .limit(26);
    const page = rows.slice(0, 25);
    return { clients: page, nextCursor: rows.length > 25 ? page.at(-1)!.id : null };
  });
}
export function getClient(db: Db, by: Principal, id: string) {
  return withProjectContext(db, by, async (tx) => {
    const [row] = await tx
      .select(fields)
      .from(clients)
      .where(and(visible(by), eq(clients.id, id)))
      .limit(1);
    if (!row) throw new AppError(404, "CLIENT_NOT_FOUND", "No such client.");
    return row;
  });
}
