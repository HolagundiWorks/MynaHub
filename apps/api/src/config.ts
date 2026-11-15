import { z } from "zod";

/**
 * Everything the API needs from its environment. The process refuses to start when something is missing or wrong, and the error names the
 * variables only, never their values (guidelines section 15: no secrets in logs).
 */
const url = z.string().regex(/^postgres(ql)?:\/\//, "must be a postgres:// or postgresql:// URL");

const schema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]).default("production"),
  PORT: z.coerce.number().int().min(1).max(65535).default(3000),
  HOST: z.string().min(1).default("0.0.0.0"),
  /** The address clients use to reach this API, for example https://api.company.example (guidelines section 10.2). */
  PUBLIC_API_URL: z.url(),
  /** Runtime connection: the role myna_runtime, which may read and write data but not change the schema. */
  DATABASE_URL: url,
  /** Migration connection: the schema owner. Used only by the migrate command, never by the running server. */
  DIRECT_DATABASE_URL: url.optional(),
  /** Signs and encrypts sessions and two-factor secrets. At least 32 characters; generate one with: openssl rand -base64 32. Never reuse it elsewhere. */
  BETTER_AUTH_SECRET: z
    .string()
    .min(32, "must be at least 32 characters")
    .refine((v) => !/change_?me/i.test(v), "is still the placeholder from .env.example"),
  /** Extra origins allowed to call the auth routes with cookies (for example the MynaHub web origin), comma separated. The API address is always trusted. */
  TRUSTED_ORIGINS: z
    .string()
    .optional()
    .transform((v) =>
      (v ?? "")
        .split(",")
        .map((x) => x.trim())
        .filter(Boolean),
    ),
  /** Administrators must have two-factor authentication before they may use administrative endpoints (guidelines section 6.4). */
  REQUIRE_ADMIN_MFA: z
    .enum(["true", "false"])
    .default("true")
    .transform((v) => v === "true"),
  LOG_LEVEL: z.enum(["fatal", "error", "warn", "info", "debug", "trace", "silent"]).default("info"),
});

export type Config = Readonly<z.infer<typeof schema>>;

export class ConfigError extends Error {
  constructor(public readonly problems: string[]) {
    super(`Invalid configuration: ${problems.join("; ")}`);
    this.name = "ConfigError";
  }
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const parsed = schema.safeParse(env);
  if (!parsed.success) {
    throw new ConfigError(parsed.error.issues.map((i) => `${i.path.join(".") || "(root)"}: ${i.message}`));
  }
  const config = parsed.data;
  if (config.NODE_ENV === "production" && !config.PUBLIC_API_URL.startsWith("https://")) {
    throw new ConfigError(["PUBLIC_API_URL: must be https in production"]);
  }
  return Object.freeze(config);
}
