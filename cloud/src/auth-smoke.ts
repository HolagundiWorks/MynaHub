import { randomBytes } from "node:crypto";
import { loadDotEnv } from "./loadEnv.js";
import { runAuthEmailSmoke } from "./services/auth.js";

loadDotEnv();

function requireEnv(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`Missing ${name}. Run neon deploy / neon env pull after auth: true.`);
  return v;
}

async function main(): Promise<void> {
  const baseUrl = requireEnv("NEON_AUTH_BASE_URL");
  const email =
    process.env.AUTH_TEST_EMAIL ??
    `o-studio-neon-auth+${Date.now()}.${randomBytes(4).toString("hex")}@mailinator.com`;
  const password = process.env.AUTH_TEST_PASSWORD ?? `O-Studio!${randomBytes(12).toString("base64url")}`;

  console.log("[auth] Managed Better Auth email sign-up smoke…");
  const result = await runAuthEmailSmoke(baseUrl, email, password, "MynaHub Auth Smoke");
  console.log("[auth] sign-up/sign-in OK");
  console.log(`[auth] user id: ${result.userId}`);
  console.log(`[auth] email: ${result.email}`);
  console.log(`[auth] getSession user id: ${result.sessionUserId ?? "(none)"}`);
  if (result.sessionUserId !== result.userId) {
    throw new Error("Session user id does not match signed-up user");
  }
  console.log("[auth] getSession matches current user.");
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
