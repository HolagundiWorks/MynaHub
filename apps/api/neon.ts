import { defineConfig } from "@neon/config/v1";

const required = (name: string) => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing Function setting: ${name}`);
  return value;
};

const env = Object.fromEntries(
  [
    "PUBLIC_API_URL",
    "MYNA_RUNTIME_DATABASE_URL",
    "BETTER_AUTH_SECRET",
    "ATTACHMENTS_BUCKET",
    "AWS_ENDPOINT_URL_S3",
    "AWS_REGION",
    "AWS_ACCESS_KEY_ID",
    "AWS_SECRET_ACCESS_KEY",
  ].map((name) => [name, required(name)]),
);

export default defineConfig({
  functions: {
    mynahub: {
      name: "MynaHub company API",
      source: "dist-function/index.mjs",
      bundler: "none",
      env: { ...env, NODE_ENV: "production", LOG_LEVEL: "warn", REQUIRE_ADMIN_MFA: "true" },
    },
  },
});
