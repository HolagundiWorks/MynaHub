import { readFileSync, writeFileSync } from "node:fs";
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

for (const name of files) {
  const path = join(root, name);
  let text = readFileSync(path, "utf8");
  text = text.replaceAll("state: State<AppState>", "state: HostState<'_>");
  text = text.replaceAll("use tauri::State;\n", "");
  if (name !== "commands.rs") {
    text = text.replace("use crate::commands::{", "use crate::commands::{HostState, ");
  }
  writeFileSync(path, text);
  console.log("updated", name);
}

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

const pattern = /#\[tauri::command\]\s+pub fn (\w+)\((.*?)\)\s*->/gs;
const arms = [];
for (const name of files) {
  const module = name.slice(0, -3);
  const text = readFileSync(join(root, name), "utf8");
  for (const match of text.matchAll(pattern)) {
    const fn = match[1];
    const callArgs = [];
    for (const raw of splitParams(match[2])) {
      if (raw.startsWith("state:")) {
        callArgs.push("HostState::new(state)");
        continue;
      }
      const pname = raw.split(":")[0].trim();
      callArgs.push(`arg(args, "${pname}")?`);
    }
    arms.push(`        "${fn}" => run(crate::${module}::${fn}(${callArgs.join(", ")})),`);
  }
}

const body = `//! JSON entry points for every Tauri command. The WinUI host calls these by name.
use crate::commands::{AppState, HostState};
use serde::Serialize;
use serde_json::Value;

pub enum BridgeError {
    Domain(String),
    BadRequest(String),
}

fn arg<T: serde::de::DeserializeOwned>(args: &Value, key: &str) -> Result<T, BridgeError> {
    let value = args.get(key).cloned().unwrap_or(Value::Null);
    serde_json::from_value(value).map_err(|error| BridgeError::BadRequest(format!("{key} is invalid: {error}")))
}

fn run<T: Serialize>(result: Result<T, String>) -> Result<Option<Vec<u8>>, BridgeError> {
    let value = result.map_err(BridgeError::Domain)?;
    serde_json::to_vec(&value).map(Some).map_err(|error| BridgeError::Domain(error.to_string()))
}

pub fn invoke(state: &AppState, op: &str, args: &Value) -> Result<Option<Vec<u8>>, BridgeError> {
    match op {
${arms.join("\n")}
        _ => Ok(None),
    }
}
`;
writeFileSync(join(root, "bridge.rs"), body);
console.log("arms", arms.length);
