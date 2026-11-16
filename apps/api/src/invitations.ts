import { and, desc, eq, gt } from "drizzle-orm";
import type { Auth } from "./auth.js";
import { recordAudit } from "./audit.js";
import { invitations, membershipRoles, memberships, roles } from "./db/schema/index.js";
import type { Db } from "./db/types.js";
import { AppError } from "./errors.js";
import { accountExists, createCredentialUser, removeUserQuietly } from "./identity.js";
import { OWNER_ROLE, roleNames } from "./organization.js";
import type { Principal } from "./principal.js";
import { hashToken, newToken } from "./tokens.js";
import { assertInvitationRole } from "./authz/roles.js";

export const INVITATION_DAYS = 7;

const invalid = () => new AppError(400, "INVITATION_INVALID", "This invitation is not valid.");

/**
 * Invites one email to one role. The token is returned once and stored only as a hash. An earlier pending invitation for the same address is
 * revoked, so there is always at most one live token per person.
 */
export async function createInvitation(
  db: Db,
  by: Principal,
  input: { email: string; roleId: string },
  requestId: string,
) {
  const email = input.email.toLowerCase();
  const role = await assertInvitationRole(db, by, input.roleId);
  // Only an Owner can make another Owner: an administrator cannot promote anyone (or themselves) beyond their own authority.
  if (role.name === OWNER_ROLE && !(await roleNames(db, by.membershipId)).includes(OWNER_ROLE)) {
    throw new AppError(403, "PERMISSION_DENIED", "Only an Owner can invite an Owner.");
  }
  if (await accountExists(db, email))
    throw new AppError(409, "ACCOUNT_EXISTS", "That email already has an account.");

  const token = newToken();
  const expiresAt = new Date(Date.now() + INVITATION_DAYS * 24 * 60 * 60 * 1000);
  const id = await db.transaction(async (tx) => {
    await tx
      .update(invitations)
      .set({ status: "revoked", revokedAt: new Date() })
      .where(
        and(
          eq(invitations.organizationId, by.organizationId),
          eq(invitations.email, email),
          eq(invitations.status, "pending"),
        ),
      );
    const [row] = await tx
      .insert(invitations)
      .values({
        organizationId: by.organizationId,
        email,
        roleId: role.id,
        tokenHash: hashToken(token),
        createdBy: by.userId,
        expiresAt,
      })
      .returning({ id: invitations.id });
    if (!row) throw new Error("invitation was not created");
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: "invitation.created",
      resourceType: "invitation",
      resourceId: row.id,
      requestId,
      detail: { email, role: role.name },
    });
    return row.id;
  });
  return { invitationId: id, token, expiresAt };
}

export async function listInvitations(db: Db, organizationId: string) {
  return db
    .select({
      id: invitations.id,
      email: invitations.email,
      role: roles.name,
      status: invitations.status,
      createdAt: invitations.createdAt,
      expiresAt: invitations.expiresAt,
    })
    .from(invitations)
    .innerJoin(roles, eq(roles.id, invitations.roleId))
    .where(eq(invitations.organizationId, organizationId))
    .orderBy(desc(invitations.createdAt));
}

export async function revokeInvitation(db: Db, by: Principal, invitationId: string, requestId: string) {
  const done = await db.transaction(async (tx) => {
    const rows = await tx
      .update(invitations)
      .set({ status: "revoked", revokedAt: new Date() })
      .where(
        and(
          eq(invitations.id, invitationId),
          eq(invitations.organizationId, by.organizationId),
          eq(invitations.status, "pending"),
        ),
      )
      .returning({ id: invitations.id });
    if (rows.length === 0) return false;
    await recordAudit(tx, {
      organizationId: by.organizationId,
      actor: by.userId,
      action: "invitation.revoked",
      resourceType: "invitation",
      resourceId: invitationId,
      requestId,
    });
    return true;
  });
  if (!done) throw new AppError(404, "INVITATION_NOT_FOUND", "No pending invitation with that id.");
}

/**
 * Spends an invitation: creates the account and an active membership with the invited role. Every failure looks the same to the caller (invalid,
 * expired, already used, revoked), so a token cannot be probed. The invitation is spent in one atomic step; two simultaneous attempts cannot both win.
 */
export async function acceptInvitation(
  deps: { db: Db; auth: Auth },
  input: { token: string; name: string; password: string },
  requestId: string,
) {
  const { db, auth } = deps;
  const hash = hashToken(input.token);
  const [inv] = await db
    .select({
      id: invitations.id,
      organizationId: invitations.organizationId,
      email: invitations.email,
      roleId: invitations.roleId,
    })
    .from(invitations)
    .where(
      and(
        eq(invitations.tokenHash, hash),
        eq(invitations.status, "pending"),
        gt(invitations.expiresAt, new Date()),
      ),
    )
    .limit(1);
  if (!inv) throw invalid();
  if (await accountExists(db, inv.email)) throw invalid();

  let account;
  try {
    account = await createCredentialUser(auth, {
      email: inv.email,
      name: input.name,
      password: input.password,
    });
  } catch {
    throw invalid(); // for example another request created this account a moment ago
  }
  try {
    return await db.transaction(async (tx) => {
      const spent = await tx
        .update(invitations)
        .set({ status: "accepted", acceptedAt: new Date(), acceptedUserId: account.id })
        .where(
          and(
            eq(invitations.id, inv.id),
            eq(invitations.status, "pending"),
            gt(invitations.expiresAt, new Date()),
          ),
        )
        .returning({ id: invitations.id });
      if (spent.length === 0) throw invalid();
      const [member] = await tx
        .insert(memberships)
        .values({ organizationId: inv.organizationId, authUserId: account.id, status: "active" })
        .returning({ id: memberships.id });
      if (!member) throw new Error("membership was not created");
      await tx.insert(membershipRoles).values({ membershipId: member.id, roleId: inv.roleId });
      await recordAudit(tx, {
        organizationId: inv.organizationId,
        actor: account.id,
        action: "invitation.accepted",
        resourceType: "invitation",
        resourceId: inv.id,
        requestId,
        detail: { email: inv.email },
      });
      return { userId: account.id, membershipId: member.id, email: inv.email };
    });
  } catch (err) {
    await removeUserQuietly(auth, account.id);
    throw err;
  }
}
