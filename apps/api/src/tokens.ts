import { createHash, randomBytes } from "node:crypto";

/** A cryptographically random token (256 bits) for setup and invitations. Shown to its recipient once; only its hash is ever stored. */
export function newToken(): string {
  return randomBytes(32).toString("base64url");
}

/** SHA-256 of a token. Enough for a 256-bit random secret (there is nothing to brute-force); the database never holds the token itself. */
export function hashToken(token: string): string {
  return createHash("sha256").update(token).digest("hex");
}
