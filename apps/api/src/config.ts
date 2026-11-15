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
