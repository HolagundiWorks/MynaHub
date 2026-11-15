import { getTableColumns } from "drizzle-orm";
import { getAuthTables } from "better-auth/db";
import { describe, expect, it } from "vitest";
import { buildAuthOptions } from "../src/auth-options.js";
import { loadConfig } from "../src/config.js";
import * as schema from "../src/db/schema/index.js";

// Guards the hand-maintained Drizzle tables in src/db/schema/auth.ts against the pinned Better Auth version: if an upgrade adds or renames a field,
// this fails and the schema must be regenerated and migrated (guidelines section 7.2).
describe("auth schema", () => {
  const config = loadConfig({
    NODE_ENV: "test",
    PUBLIC_API_URL: "https://api.company.example",
    DATABASE_URL: "postgresql://x/x",
    BETTER_AUTH_SECRET: "x".repeat(40),
  });
  const tables = getAuthTables(buildAuthOptions({} as never, config)) as Record<
    string,
    { modelName: string; fields: Record<string, unknown> }
  >;

  it("has a Drizzle table for every table Better Auth uses, with every field", () => {
    const drizzleTables = schema as unknown as Record<string, Parameters<typeof getTableColumns>[0]>;
    for (const [model, def] of Object.entries(tables)) {
      const table = drizzleTables[def.modelName] ?? drizzleTables[model];
      expect(table, `no Drizzle table for Better Auth model "${model}"`).toBeDefined();
      const columns = Object.keys(getTableColumns(table as never));
      for (const field of Object.keys(def.fields)) {
        expect(columns, `${model}.${field} is missing from the Drizzle schema`).toContain(field);
      }
      expect(columns).toContain("id");
    }
  });

  it("uses exactly the models this build expects (no plugin added a table unnoticed)", () => {
    expect(Object.keys(tables).sort()).toEqual(["account", "session", "twoFactor", "user", "verification"]);
  });
});
