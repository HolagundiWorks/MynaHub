import { and, eq, gt, isNull, sql } from "drizzle-orm";
import type { Auth } from "./auth.js";
import { recordAudit } from "./audit.js";
import { bootstrapTokens, membershipRoles, memberships } from "./db/schema/index.js";
import type { Db } from "./db/types.js";
import { AppError } from "./errors.js";
import { createCredentialUser, removeUserQuietly } from "./identity.js";
import { getOrganization, OWNER_ROLE, seedOrganization } from "./organization.js";
import { hashToken, newToken } from "./tokens.js";

// Serialises bootstrap attempts so two requests cannot both create the first organisation.
const BOOTSTRAP_LOCK = 727_001;

const complete = () => new AppError(409, "BOOTSTRAP_COMPLETE", "This company has already been set up.");
const invalid = () => new AppError(400, "SETUP_TOKEN_INVALID", "The setup token is not valid.");

/**
 * Makes a one-time setup token (guidelines section 5.3): random, stored only as a hash, short-lived, single use. Earlier unused tokens stop working.
 * Refuses once the company is set up. The caller (the operator of the deployment) shows the token once; it is not recoverable.
 */
export async function createBootstrapToken(
  db: Db,
  minutes = 60,
): Promise<{ token: string; expiresAt: Date }> {
  if (await getOrganization(db)) throw complete();
  const ttl = Math.min(Math.max(Math.trunc(minutes), 5), 24 * 60);
  const token = newToken();
  const expiresAt = new Date(Date.now() + ttl * 60_000);
  await db.update(bootstrapTokens).set({ usedAt: new Date() }).where(isNull(bootstrapTokens.usedAt));
  await db.insert(bootstrapTokens).values({ tokenHash: hashToken(token), expiresAt });
  return { token, expiresAt };
}

export interface BootstrapInput {
  setupToken: string;
  organizationName: string;
  slug: string;
  adminName: string;
  adminEmail: string;
  adminPassword: string;
}

/**
 * Creates the organisation, its roles and the first administrator (the Owner), then closes bootstrap for good. No default password, no backdoor:
 * the account exists only if the setup token is valid, and the token can be spent once.
 */
export async function completeBootstrap(
  deps: { db: Db; auth: Auth },
  input: BootstrapInput,
  requestId: string,
) {
  const { db, auth } = deps;
  if (await getOrganization(db)) throw complete();

  const hash = hashToken(input.setupToken);
  const [candidate] = await db
    .select({ id: bootstrapTokens.id })
    .from(bootstrapTokens)
    .where(
      and(
        eq(bootstrapTokens.tokenHash, hash),
        isNull(bootstrapTokens.usedAt),
        gt(bootstrapTokens.expiresAt, new Date()),
      ),
    )
    .limit(1);
  if (!candidate) throw invalid();

  const account = await createCredentialUser(auth, {
    email: input.adminEmail,
    name: input.adminName,
    password: input.adminPassword,
  });
  try {
    return await db.transaction(async (tx) => {
      await tx.execute(sql`select pg_advisory_xact_lock(${BOOTSTRAP_LOCK})`);
      if (await getOrganization(tx)) throw complete();
      const spent = await tx
        .update(bootstrapTokens)
        .set({ usedAt: new Date() })
        .where(
          and(
            eq(bootstrapTokens.tokenHash, hash),
            isNull(bootstrapTokens.usedAt),
            gt(bootstrapTokens.expiresAt, new Date()),
          ),
        )
        .returning({ id: bootstrapTokens.id });
      if (spent.length === 0) throw invalid();

      const { organizationId, roleIds } = await seedOrganization(tx, {
        name: input.organizationName,
        slug: input.slug,
      });
      const [member] = await tx
        .insert(memberships)
        .values({ organizationId, authUserId: account.id, status: "active" })
        .returning({ id: memberships.id });
      if (!member) throw new Error("membership was not created");
      await tx
        .insert(membershipRoles)
        .values({ membershipId: member.id, roleId: roleIds[OWNER_ROLE] as string });
      await recordAudit(tx, {
        organizationId,
        actor: account.id,
        action: "bootstrap.completed",
        resourceType: "organization",
        resourceId: organizationId,
        requestId,
        detail: { organizationName: input.organizationName },
      });
      return { organizationId, userId: account.id, membershipId: member.id };
    });
  } catch (err) {
    await removeUserQuietly(auth, account.id);
    throw err;
  }
}
