import { createAttachmentStore } from "./attachment-store.js";
import { fileURLToPath } from "node:url";
import { ConfigError, loadConfig } from "./config.js";
import { initializeEnvironment, setupDatabase, SetupError } from "./setup.js";

const mode = process.argv[2] ?? "check";
try {
  if (mode === "init") {
    await initializeEnvironment(fileURLToPath(new URL("../.env.example", import.meta.url)), ".env");
    process.stdout.write(
      "Created .env with a new signing secret. Fill the public API URL and both database URLs privately. Protect this file with your OS account permissions.\n",
    );
  } else if (mode === "check" || mode === "apply") {
    const config = loadConfig();
    const state = await setupDatabase(
      config,
      fileURLToPath(new URL("../drizzle", import.meta.url)),
      mode === "apply",
    );
    process.stdout.write("Database roles, table grants, migration hashes and readiness access passed.\n");
    const storage = createAttachmentStore(config);
    if (storage) {
      try {
        await storage.check();
      } catch {
        throw new SetupError(
          "Private attachment storage is unavailable or public. Check the branch, bucket and scoped credential.",
        );
      }
      process.stdout.write("Configured private attachment storage passed its access check.\n");
    }
    process.stdout.write(
      state === "bootstrap"
        ? "Next: generate a one-time token with bootstrap:token, then create the first administrator through POST /v1/bootstrap.\n"
        : "Company already configured. Bootstrap stays closed; existing accounts and secrets were preserved.\n",
    );
    process.stdout.write(
      "Deploy the API behind HTTPS. Mobile uses this API online; native desktop integration remains pending. See docs/hub-platform-plan.md.\n",
    );
  } else {
    throw new SetupError(
      "Usage: setup [init|check|apply]. Check is read-only; apply runs migrations and grants readiness metadata access.",
    );
  }
} catch (err) {
  process.stderr.write(
    `${err instanceof SetupError || err instanceof ConfigError ? err.message : "Setup failed; no private error details are logged."}\n`,
  );
  process.exitCode = 2;
}
