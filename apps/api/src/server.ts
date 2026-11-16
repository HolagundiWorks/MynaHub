import { createAttachmentStore } from "./attachment-store.js";
import { fileURLToPath } from "node:url";
import { buildApp } from "./app.js";
import { createAuth } from "./auth.js";
import { ConfigError, loadRuntimeConfig } from "./config.js";
import { connect } from "./db/client.js";
import { shippedMigrations } from "./health.js";
import { checkRuntimeRole } from "./setup.js";

const VERSION = process.env.MYNA_API_VERSION ?? "0.1.0";

let config;
try {
  config = loadRuntimeConfig();
} catch (err) {
  if (err instanceof ConfigError) {
    process.stderr.write(`${err.message}\n`);
    process.exit(2);
  }
  throw err;
}

const database = connect(config.DATABASE_URL);
try {
  await checkRuntimeRole(database.pool);
} catch {
  await database.close();
  process.stderr.write(
    "Runtime database access check failed. Run the setup check with operator credentials.\n",
  );
  process.exit(2);
}
const journal = fileURLToPath(new URL("../drizzle/meta/_journal.json", import.meta.url));
const app = await buildApp({
  config,
  storage: createAttachmentStore(config),
  health: database.health,
  db: database.db,
  auth: createAuth(database.db, config),
  expectedMigrations: shippedMigrations(journal),
  version: VERSION,
});

async function shutdown(signal: string) {
  app.log.info({ signal }, "shutting down");
  await app.close();
  await database.close();
  process.exit(0);
}
process.on("SIGTERM", () => void shutdown("SIGTERM"));
process.on("SIGINT", () => void shutdown("SIGINT"));

await app.listen({ host: config.HOST, port: config.PORT });
