import { attachDatabasePool } from "@neon/functions";
import { createAttachmentStore } from "./attachment-store.js";
import { buildApp } from "./app.js";
import { createAuth } from "./auth.js";
import { loadFunctionConfig } from "./config.js";
import { connect } from "./db/client.js";
import { functionHandler } from "./function-handler.js";
import { checkRuntimeRole } from "./setup.js";

// Embedded from the shipped journal by scripts/build-function.mjs.
declare const MYNA_FUNCTION_MIGRATIONS: number;
let handler: Promise<(request: Request) => Promise<Response>> | undefined;

async function initialize() {
  // Neon injects a database URL. Never rely on its role being the restricted app role.
  const config = loadFunctionConfig();
  const database = connect(config.DATABASE_URL, 5);
  attachDatabasePool(database.pool, {
    onUnexpectedError: () => process.stderr.write("Database pool unavailable\n"),
  });
  try {
    await checkRuntimeRole(database.pool);
    const app = await buildApp({
      config,
      db: database.db,
      health: database.health,
      auth: createAuth(database.db, config),
      storage: createAttachmentStore(config),
      expectedMigrations: MYNA_FUNCTION_MIGRATIONS,
      version: process.env.MYNA_API_VERSION ?? "0.1.0",
    });
    await app.ready();
    return functionHandler(app);
  } catch (error) {
    await database.close();
    throw error;
  }
}

export default {
  async fetch(request: Request): Promise<Response> {
    handler ??= initialize().catch(() => {
      handler = undefined;
      throw new Error("API initialization unavailable");
    });
    try {
      return await (
        await handler
      )(request);
    } catch {
      return new Response(null, { status: 503, headers: { "cache-control": "no-store" } });
    }
  },
};
