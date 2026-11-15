import { fileURLToPath } from "node:url";
import { loadConfig } from "../config.js";
import { runMigrations } from "./migrate.js";

const folder = fileURLToPath(new URL("../../drizzle", import.meta.url));
const config = loadConfig();
if (!config.DIRECT_DATABASE_URL) {
  process.stderr.write("DIRECT_DATABASE_URL (the schema owner connection) is required to migrate.\n");
  process.exit(2);
}
await runMigrations(config.DIRECT_DATABASE_URL, folder);
process.stdout.write("Migrations applied.\n");
