import { and, eq } from "drizzle-orm";
import type { FastifyReply, FastifyRequest } from "fastify";
import { fromNodeHeaders } from "better-auth/node";
import type { Auth } from "./auth.js";
import type { Config } from "./config.js";
import { memberships } from "./db/schema/index.js";
import type { Db } from "./db/types.js";
import { AppError } from "./errors.js";
import { ADMIN_PERMISSIONS, effectivePermissions, getOrganization } from "./organization.js";

/** Who is making the request, established on every protected request (never trusted from the client). */
export interface Principal {
  userId: string;
  email: string;
  name: string;
  twoFactorEnabled: boolean;
  organizationId: string;
  membershipId: string;
  permissions: ReadonlySet<string>;
  sessionToken: string;
  sessionCreatedAt: Date;
}

declare module "fastify" {
  interface FastifyRequest {
    principal?: Principal;
  }
}

/** A session this recent counts as fresh: sensitive operations ask for it (section 6.4). Matches the auth configuration. */
export const FRESH_SECONDS = 5 * 60;

export function createGuards(deps: { auth: Auth; db: Db; config: Config }) {
  const { auth, db, config } = deps;

  /** Validates the session AND the membership on every call: a suspended or removed person loses access on the next request. */
  async function authenticate(req: FastifyRequest): Promise<Principal> {
    const found = await auth.api.getSession({
      headers: fromNodeHeaders(req.headers),
      query: { disableCookieCache: true },
    });
    if (!found) throw new AppError(401, "UNAUTHENTICATED", "Sign in to continue.");
    const org = await getOrganization(db);
    if (!org) throw new AppError(409, "NOT_INITIALIZED", "This company has not been set up yet.");
    const [member] = await db
      .select({ id: memberships.id, status: memberships.status })
      .from(memberships)
      .where(and(eq(memberships.organizationId, org.id), eq(memberships.authUserId, found.user.id)))
      .limit(1);
    if (!member) throw new AppError(403, "NOT_A_MEMBER", "This account is not a member of the company.");
    if (member.status === "suspended")
      throw new AppError(403, "MEMBERSHIP_SUSPENDED", "This account has been suspended.");
    if (member.status !== "active")
      throw new AppError(403, "MEMBERSHIP_INACTIVE", "This membership is not active.");
    const principal: Principal = {
      userId: found.user.id,
      email: found.user.email,
      name: found.user.name,
      twoFactorEnabled: Boolean((found.user as { twoFactorEnabled?: boolean | null }).twoFactorEnabled),
      organizationId: org.id,
      membershipId: member.id,
      permissions: await effectivePermissions(db, member.id),
      sessionToken: found.session.token,
      sessionCreatedAt: new Date(found.session.createdAt),
    };
    req.principal = principal;
    return principal;
  }

  /** preHandler: a signed-in, active member. */
  const requireMember = async (req: FastifyRequest, _reply: FastifyReply) => {
    await authenticate(req);
  };

  /** preHandler: an active member holding `permission`. Administrative permissions also need two-factor authentication when configured. */
  const requirePermission = (permission: string) => async (req: FastifyRequest, _reply: FastifyReply) => {
    const p = await authenticate(req);
    if (!p.permissions.has(permission)) throw new AppError(403, "PERMISSION_DENIED", "You cannot do this.");
    if (config.REQUIRE_ADMIN_MFA && ADMIN_PERMISSIONS.has(permission) && !p.twoFactorEnabled) {
      throw new AppError(
        403,
        "MFA_REQUIRED",
        "Turn on two-factor authentication to use administrative functions.",
      );
    }
  };

  /** preHandler (after one of the above): the session was created recently; otherwise sign in again. */
  const requireFresh = async (req: FastifyRequest, _reply: FastifyReply) => {
    const p = req.principal;
    if (!p) throw new AppError(401, "UNAUTHENTICATED", "Sign in to continue.");
    if (Date.now() - p.sessionCreatedAt.getTime() > FRESH_SECONDS * 1000) {
      throw new AppError(403, "REAUTH_REQUIRED", "Sign in again to do this.");
    }
  };

  return { authenticate, requireMember, requirePermission, requireFresh };
}

export type Guards = ReturnType<typeof createGuards>;
