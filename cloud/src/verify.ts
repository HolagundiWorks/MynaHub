import { loadDotEnv } from "./loadEnv.js";
import { verifyPostgres } from "./services/postgres.js";
import { createStorageClient, verifyObjectStorage } from "./services/storage.js";
import { verifyHelloFunction } from "./services/functions.js";
import { verifyRealtime } from "./services/realtime.js";

loadDotEnv();

function requireEnv(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`Missing ${name}. Run neon deploy and keep .env.local, or use: neon run npm start`);
  return v;
}

function postgresUrl(): string {
  return (
    process.env.DATABASE_URL_POOLED ??
    process.env.DATABASE_URL ??
    process.env.DATABASE_URL_UNPOOLED ??
    ""
  );
}

async function main(): Promise<void> {
  const dbUrl = postgresUrl();
  if (!dbUrl) throw new Error("Missing DATABASE_URL or DATABASE_URL_POOLED");
  const rtUrl = requireEnv("NEON_REALTIME_URL");
  const rtSecret = requireEnv("NEON_REALTIME_SECRET");
  requireEnv("AWS_ENDPOINT_URL_S3");
  requireEnv("AWS_REGION");
  requireEnv("AWS_ACCESS_KEY_ID");
  requireEnv("AWS_SECRET_ACCESS_KEY");
  requireEnv("S3_BUCKET");

  console.log("Verifying Neon services for MynaHub cloud sync…");
  await verifyPostgres(dbUrl);
  await verifyObjectStorage(createStorageClient());

  const helloUrl = process.env.NEON_FUNCTION_HELLO_BASE_URL;
  if (helloUrl) {
    await verifyHelloFunction(helloUrl);
  } else {
    console.log("[functions] skipped — set NEON_FUNCTION_HELLO_BASE_URL after neon deploy");
  }

  await verifyRealtime(
    process.env.DATABASE_URL_UNPOOLED ?? process.env.DATABASE_URL ?? dbUrl,
    rtUrl,
    rtSecret,
  );
  console.log("All wired Neon services passed.");
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
