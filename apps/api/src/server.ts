import { fileURLToPath } from "node:url";
import { buildApp } from "./app.js";
import { ConfigError, loadConfig } from "./config.js";
import { connect } from "./db/client.js";
import { shippedMigrations } from "./health.js";

const VERSION = process.env.MYNA_API_VERSION ?? "0.1.0";

let config;
try {
  config = loadConfig();
} catch (err) {
  if (err instanceof ConfigError) {
    process.stderr.write(`${err.message}\n`);
    process.exit(2);
  }
  throw err;
}

const database = connect(config.DATABASE_URL);
const journal = fileURLToPath(new URL("../drizzle/meta/_journal.json", import.meta.url));
const app = await buildApp({
  config,
  health: database.health,
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
