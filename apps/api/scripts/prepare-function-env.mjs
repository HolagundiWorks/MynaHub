import { readFile, writeFile } from "node:fs/promises";
import { basename, resolve } from "node:path";
import { parseEnv } from "node:util";

const [source, destination, origin] = process.argv.slice(2);
try {
  if (!source || !destination || !origin || !basename(destination).startsWith(".env.") || resolve(source) === resolve(destination)) throw new Error();
  const url = new URL(origin);
  if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash || url.pathname !== "/") throw new Error();
  const sourceEnv = parseEnv(await readFile(source, "utf8"));
  const settings = {
    PUBLIC_API_URL: url.origin,
    MYNA_RUNTIME_DATABASE_URL: sourceEnv.DATABASE_URL,
    BETTER_AUTH_SECRET: sourceEnv.BETTER_AUTH_SECRET,
    ATTACHMENTS_BUCKET: sourceEnv.ATTACHMENTS_BUCKET,
    AWS_ENDPOINT_URL_S3: sourceEnv.AWS_ENDPOINT_URL_S3,
    AWS_REGION: sourceEnv.AWS_REGION,
    AWS_ACCESS_KEY_ID: sourceEnv.AWS_ACCESS_KEY_ID,
    AWS_SECRET_ACCESS_KEY: sourceEnv.AWS_SECRET_ACCESS_KEY,
  };
  if (Object.values(settings).some(value => !value || /[\r\n]/.test(value))) throw new Error();
  const body = Object.entries(settings).map(([key, value]) => `${key}=${JSON.stringify(value)}`).join("\n") + "\n";
  await writeFile(destination, body, { flag: "wx", mode: 0o600 });
  process.stdout.write("Private Function environment created; existing files are never overwritten.\n");
} catch {
  process.stderr.write("Could not prepare Function settings. Provide source, new .env.* destination and HTTPS origin; check required values and permissions. Existing files are never overwritten.\n");
  process.exitCode = 2;
}
