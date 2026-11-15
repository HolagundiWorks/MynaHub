// Used only by the Better Auth schema generator (npm run auth:generate). It builds the same options with a placeholder database; nothing connects.
import { betterAuth } from "better-auth";
import { buildAuthOptions } from "./auth-options.js";
import { loadConfig } from "./config.js";

const config = loadConfig({
  NODE_ENV: "development",
  PUBLIC_API_URL: "http://localhost:3000",
  DATABASE_URL: "postgresql://placeholder",
  BETTER_AUTH_SECRET: "placeholder-secret-placeholder-secret-0000",
});

export const auth = betterAuth(buildAuthOptions({} as never, config));
