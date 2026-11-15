import { defineConfig } from "drizzle-kit";

// Used only by `npm run db:generate` (writes SQL migrations into ./drizzle from the schema in src/db/schema). It never connects to a database.
export default defineConfig({
  dialect: "postgresql",
  schema: "./src/db/schema/index.ts",
  out: "./drizzle",
});
