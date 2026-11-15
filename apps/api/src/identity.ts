import { eq } from "drizzle-orm";
import type { Auth } from "./auth.js";
import type { Db } from "./db/types.js";
import { user } from "./db/schema/index.js";

/**
 * Creating an account is the one thing public registration is switched off for. It happens here, server-side, only after the caller has proved a
 * right to it (a valid setup token or invitation). It uses Better Auth's own internal adapter and password hasher, not raw SQL.
 */
export async function createCredentialUser(
  auth: Auth,
  input: { email: string; name: string; password: string },
) {
  const ctx = await auth.$context;
  const passwordHash = await ctx.password.hash(input.password);
  const created = await ctx.internalAdapter.createUser(
    { email: input.email.toLowerCase(), name: input.name, emailVerified: false },
    { method: "admin" },
  );
  try {
    await ctx.internalAdapter.linkAccount({
      userId: created.id,
      providerId: "credential",
      accountId: created.id,
      password: passwordHash,
    });
  } catch (err) {
    await ctx.internalAdapter.deleteUser(created.id);
    throw err;
  }
  return { id: created.id, email: created.email };
}

/** Undo for a step that failed after the account was created. Never throws: the original failure is the one to report. */
export async function removeUserQuietly(auth: Auth, userId: string): Promise<void> {
  try {
    await (await auth.$context).internalAdapter.deleteUser(userId);
  } catch {
    // The caller is already handling a failure; a leftover account without a membership has no access to anything.
  }
}

export async function accountExists(db: Db, email: string): Promise<boolean> {
  const rows = await db
    .select({ id: user.id })
    .from(user)
    .where(eq(user.email, email.toLowerCase()))
    .limit(1);
  return rows.length > 0;
}

/** Ends every session of an account at once (suspension, forced sign-out). */
export async function revokeAllSessions(auth: Auth, userId: string): Promise<void> {
  await (await auth.$context).internalAdapter.deleteUserSessions(userId);
}
