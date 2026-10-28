/** Regenerate bridge.rs match arms from HostState entry points. */
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "src");
const files = [
  "commands.rs",
  "company_file.rs",
  "documents.rs",
  "studio.rs",
  "billing.rs",
  "receipts.rs",
  "admin.rs",
  "registers.rs",
  "bank.rs",
  "accounting.rs",
  "clients.rs",
];

function splitParams(source) {
  const parts = [];
  let buf = "";
  let depth = 0;
  for (const ch of source) {
    if (ch === "<") depth += 1;
    else if (ch === ">") depth -= 1;
    if (ch === "," && depth === 0) {
      if (buf.trim()) parts.push(buf.trim());
      buf = "";
    } else buf += ch;
  }
  if (buf.trim()) parts.push(buf.trim());
  return parts;
}

const pattern = /pub fn (\w+)\(\s*state:\s*HostState<'_>(.*?)\)\s*->/gs;
const arms = [];
for (const name of files) {
  const module = name.slice(0, -3);
  const text = readFileSync(join(root, name), "utf8");
  for (const match of text.matchAll(pattern)) {
    const fn = match[1];
    const callArgs = ["HostState::new(state)"];
    for (const raw of splitParams(match[2])) {
      if (!raw) continue;
      const pname = raw.split(":")[0].trim();
      callArgs.push(`arg(args, "${pname}")?`);
    }
    arms.push(`        "${fn}" => run(crate::${module}::${fn}(${callArgs.join(", ")})),`);
  }
}

console.log(arms.join("\n"));
console.error("arms", arms.length);
