import { readFile, mkdir, writeFile } from "node:fs/promises";
import { buildFunctionBundle } from "@neon/config-runtime/v1";
import { build } from "esbuild";

const journal = JSON.parse(await readFile(new URL("../drizzle/meta/_journal.json", import.meta.url), "utf8"));
const zip = await buildFunctionBundle({
  slug: "mynahub",
  name: "MynaHub company API",
  source: "./src/function.ts",
  env: {},
  runtime: "nodejs24",
  bundler: async () => {
    const result = await build({
      entryPoints: ["src/function.ts"], bundle: true, platform: "node", target: "node24", format: "esm", write: false,
      banner: { js: "import{createRequire as ___cr}from'node:module';import{fileURLToPath as ___f}from'node:url';import{dirname as ___d}from'node:path';const require=___cr(import.meta.url);const __filename=___f(import.meta.url);const __dirname=___d(__filename);" },
      define: { MYNA_FUNCTION_MIGRATIONS: String(journal.entries.length) },
    });
    await mkdir("dist-function", { recursive: true });
    await writeFile("dist-function/index.mjs", result.outputFiles[0].contents);
    return { "index.mjs": result.outputFiles[0].contents };
  },
});
await mkdir("dist-function", { recursive: true });
await writeFile("dist-function/function.zip", zip);
process.stdout.write(`Function bundle ready (${journal.entries.length} migrations).\n`);
