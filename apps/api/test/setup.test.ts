import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { PGlite } from "@electric-sql/pglite";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";
import {
  checkMigrations,
  checkRuntimeAccess,
  checkRuntimeRole,
  initializeEnvironment,
  validateSetup,
} from "../src/setup.js";

const env = {
  PUBLIC_API_URL: "https://api.company.example",
  DATABASE_URL: "postgresql://myna_runtime:private-pass@ep-test-pooler.neon.tech/neondb?sslmode=require",
  DIRECT_DATABASE_URL: "postgresql://owner:private-owner-pass@ep-test.neon.tech/neondb?sslmode=require",
  BETTER_AUTH_SECRET: "test-only-secret-test-only-secret-test-only",
};
const folder = fileURLToPath(new URL("../drizzle", import.meta.url));

describe("setup configuration and exclusive initialization", () => {
  it("runs the CLI without disclosing a generated secret and preserves existing settings", async () => {
    const dir = await mkdtemp(join(tmpdir(), "myna-setup-cli-"));
    const cli = fileURLToPath(new URL("../src/setup-cli.ts", import.meta.url));
    const run = (mode: string) =>
      spawnSync(process.execPath, ["--import", import.meta.resolve("tsx"), cli, mode], {
        cwd: dir,
        encoding: "utf8",
      });
    try {
      const first = run("init");
      expect(first.status).toBe(0);
      const original = await readFile(join(dir, ".env"), "utf8");
      const secret = original.match(/^BETTER_AUTH_SECRET=(.+)$/m)?.[1]?.trim();
      expect(secret?.length).toBe(64);
      expect(first.stdout.includes(secret ?? "missing")).toBe(false);
      const second = run("init");
      expect(second.status).toBe(2);
      expect(second.stderr).toContain("never overwritten");
      expect((await readFile(join(dir, ".env"), "utf8")) === original).toBe(true);
      expect(run("unknown").status).toBe(2);
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
  it("accepts a pooled runtime and matching direct owner", () => {
    expect(validateSetup(loadConfig(env)).runtime.username).toBe("myna_runtime");
  });
  it.each([
    { DIRECT_DATABASE_URL: undefined },
    { DIRECT_DATABASE_URL: env.DATABASE_URL },
    { DATABASE_URL: env.DIRECT_DATABASE_URL },
    { DIRECT_DATABASE_URL: env.DIRECT_DATABASE_URL.replace("ep-test.", "ep-other.") },
    { DIRECT_DATABASE_URL: env.DIRECT_DATABASE_URL.replace("/neondb?", "/other?") },
    { DATABASE_URL: env.DATABASE_URL.replace("sslmode=require", "sslmode=disable") },
    { DATABASE_URL: env.DATABASE_URL + "&ssl=no-verify" },
    { DATABASE_URL: env.DATABASE_URL + "&uselibpqcompat=true" },
    { DATABASE_URL: "postgresql://myna_runtime:private-pass@" },
    { REQUIRE_ADMIN_MFA: "false" },
    { TRUSTED_ORIGINS: "*" },
    { TRUSTED_ORIGINS: "http://web.company.example" },
  ])("rejects unsafe setup without echoing supplied secrets (%#)", (changes) => {
    let error = "";
    try {
      validateSetup(loadConfig({ ...env, ...changes }));
    } catch (err) {
      error = (err as Error).message;
    }
    expect(error).not.toBe("");
    expect(error).not.toContain("private-pass");
    expect(error).not.toContain("private-owner-pass");
    expect(error).not.toContain(env.BETTER_AUTH_SECRET);
  });
  it("writes a new secret but never overwrites an existing deployment", async () => {
    const dir = await mkdtemp(join(tmpdir(), "myna-setup-"));
    try {
      const template = join(dir, "example");
      const destination = join(dir, ".env");
      await writeFile(template, "BETTER_AUTH_SECRET=CHANGE_ME_TO_A_LONG_RANDOM_VALUE\n");
      await initializeEnvironment(template, destination);
      const first = await readFile(destination, "utf8");
      expect(first).toMatch(/^BETTER_AUTH_SECRET=[A-Za-z0-9_-]{64}\n$/);
      await expect(initializeEnvironment(template, destination)).rejects.toThrow(/never overwritten/);
      expect(await readFile(destination, "utf8")).toBe(first);
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});

describe("setup checks against PostgreSQL catalogs (PGlite)", () => {
  let db: PGlite;
  beforeAll(async () => {
    db = new PGlite();
    await db.exec("CREATE ROLE myna_runtime LOGIN");
    await migrate(drizzle(db), { migrationsFolder: folder });
  });
  afterAll(async () => {
    await db.close();
  });

  it("compares migration hashes and refuses a modified history", async () => {
    await checkMigrations(db, folder);
    await db.exec("BEGIN");
    try {
      await db.exec("UPDATE drizzle.__drizzle_migrations SET hash = 'different'");
      await expect(checkMigrations(db, folder)).rejects.toThrow(/do not match/);
    } finally {
      await db.exec("ROLLBACK");
    }
  });
  it("refuses the schema owner as runtime", async () => {
    await expect(checkRuntimeRole(db)).rejects.toThrow(/not isolated/);
  });
  it("accepts least privilege and catches the missing readiness grant", async () => {
    await db.exec("SET ROLE myna_runtime");
    try {
      await checkRuntimeRole(db);
      await expect(checkRuntimeAccess(db)).rejects.toThrow();
    } finally {
      await db.exec("RESET ROLE");
    }
    await db.exec(
      "GRANT USAGE ON SCHEMA drizzle TO myna_runtime; GRANT SELECT ON drizzle.__drizzle_migrations TO myna_runtime",
    );
    await db.exec("SET ROLE myna_runtime");
    try {
      await checkRuntimeAccess(db);
    } finally {
      await db.exec("RESET ROLE");
    }
  });
  it("refuses role memberships, even without inheritance", async () => {
    await db.exec("CREATE ROLE extra_role; GRANT extra_role TO myna_runtime; SET ROLE myna_runtime");
    try {
      await expect(checkRuntimeRole(db)).rejects.toThrow(/not isolated/);
    } finally {
      await db.exec("RESET ROLE; REVOKE extra_role FROM myna_runtime");
    }
  });
  it("refuses schema creation privileges and excess audit grants", async () => {
    await db.exec("GRANT CREATE ON SCHEMA public TO myna_runtime; SET ROLE myna_runtime");
    try {
      await expect(checkRuntimeRole(db)).rejects.toThrow(/not isolated/);
    } finally {
      await db.exec("RESET ROLE; REVOKE CREATE ON SCHEMA public FROM myna_runtime");
    }
    await db.exec("GRANT DELETE ON audit.events TO myna_runtime; SET ROLE myna_runtime");
    try {
      await expect(checkRuntimeAccess(db)).rejects.toThrow(/permissions/);
    } finally {
      await db.exec("RESET ROLE; REVOKE DELETE ON audit.events FROM myna_runtime");
    }
  });

  it("refuses write access to migration history", async () => {
    await db.exec("GRANT UPDATE ON drizzle.__drizzle_migrations TO myna_runtime; SET ROLE myna_runtime");
    try {
      await expect(checkRuntimeAccess(db)).rejects.toThrow(/read-only/);
    } finally {
      await db.exec("RESET ROLE; REVOKE UPDATE ON drizzle.__drizzle_migrations FROM myna_runtime");
    }
  });
});
