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
    const state = await setupDatabase(
      loadConfig(),
      fileURLToPath(new URL("../drizzle", import.meta.url)),
      mode === "apply",
    );
    process.stdout.write("Database roles, table grants, migration hashes and readiness access passed.\n");
    process.stdout.write(
      state === "bootstrap"
        ? "Next: generate a one-time token with bootstrap:token, then create the first administrator through POST /v1/bootstrap.\n"
        : "Company already configured. Bootstrap stays closed; existing accounts and secrets were preserved.\n",
    );
    process.stdout.write(
      "Deploy the API behind HTTPS. Client migration to this API is still pending; see docs/hub-platform-plan.md.\n",
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
