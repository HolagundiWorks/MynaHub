import { ConfigError, loadConfig } from "./config.js";
import { createBootstrapToken } from "./bootstrap.js";
import { AppError } from "./errors.js";
import { connect } from "./db/client.js";

// npm run bootstrap:token [-- --minutes 60]
// Prints a one-time setup token for creating the first administrator. It is shown once and cannot be recovered; only its hash is stored.
const arg = process.argv.indexOf("--minutes");
const minutes = arg >= 0 ? Number(process.argv[arg + 1]) : 60;
if (!Number.isFinite(minutes)) {
  process.stderr.write("--minutes needs a number.\n");
  process.exit(2);
}

let database;
try {
  database = connect(loadConfig().DATABASE_URL);
  const { token, expiresAt } = await createBootstrapToken(database.db, minutes);
  process.stdout.write(
    [
      "Setup token (shown once, keep it private):",
      "",
      `  ${token}`,
      "",
      `Valid until ${expiresAt.toISOString()}, for one use. Send it to POST /v1/bootstrap with the company and administrator details.`,
      "",
    ].join("\n"),
  );
} catch (err) {
  if (err instanceof ConfigError || err instanceof AppError) {
    process.stderr.write(`${err.message}\n`);
    process.exit(err instanceof AppError ? 3 : 2);
  }
  throw err;
} finally {
  await database?.close();
}
