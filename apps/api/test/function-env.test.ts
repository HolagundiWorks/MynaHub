import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { parseEnv } from "node:util";
import { expect, it } from "vitest";

it("prepares only runtime Function settings, keeps credentials private and never overwrites files", async () => {
  const dir = await mkdtemp(join(tmpdir(), "myna-function-env-"));
  const source = join(dir, ".env.source"),
    destination = join(dir, ".env.function");
  const script = fileURLToPath(new URL("../scripts/prepare-function-env.mjs", import.meta.url));
  const run = (origin = "https://demo.example") =>
    spawnSync(process.execPath, [script, source, destination, origin], { encoding: "utf8" });
  const values = {
    DATABASE_URL: "postgresql://myna_runtime:private-runtime@ep-demo.neon.tech/neondb?sslmode=require",
    DIRECT_DATABASE_URL: "postgresql://owner:private-owner@ep-demo.neon.tech/neondb?sslmode=require",
    BETTER_AUTH_SECRET: "fictional-signing-secret",
    ATTACHMENTS_BUCKET: "demo-files",
    AWS_ENDPOINT_URL_S3: "https://demo.example",
    AWS_REGION: "demo-region",
    AWS_ACCESS_KEY_ID: "fictional-access",
    AWS_SECRET_ACCESS_KEY: "fictional-secret",
  };
  try {
    await writeFile(
      source,
      Object.entries(values)
        .map(([k, v]) => `${k}=${JSON.stringify(v)}`)
        .join("\n"),
    );
    expect(run("http://demo.example").status).toBe(2);
    const first = run();
    expect(first.status).toBe(0);
    expect(first.stdout + first.stderr).not.toContain("fictional-secret");
    expect(first.stdout + first.stderr).not.toContain("private-runtime");
    const original = await readFile(destination, "utf8"),
      saved = parseEnv(original);
    expect(saved.MYNA_RUNTIME_DATABASE_URL).toBe(values.DATABASE_URL);
    expect(saved.PUBLIC_API_URL).toBe("https://demo.example");
    expect(saved).not.toHaveProperty("DIRECT_DATABASE_URL");
    expect(saved).not.toHaveProperty("DATABASE_URL");
    expect(original).not.toContain("private-owner");
    expect(run().status).toBe(2);
    expect(await readFile(destination, "utf8")).toBe(original);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
