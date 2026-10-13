import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1] / "src"
files = [
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
]

for name in files:
    path = root / name
    text = path.read_text(encoding="utf-8")
    text = text.replace("state: State<AppState>", "state: HostState<'_>")
    text = text.replace("use tauri::State;\n", "")
    if name != "commands.rs":
        text = text.replace("use crate::commands::{", "use crate::commands::{HostState, ", 1)
    path.write_text(text, encoding="utf-8")
    print("updated", name)

pattern = re.compile(r"#\[tauri::command\]\s+pub fn (\w+)\((.*?)\)\s*->", re.S)


def split_params(source: str) -> list[str]:
    parts: list[str] = []
    buf: list[str] = []
    depth = 0
    for ch in source:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        if ch == "," and depth == 0:
            part = "".join(buf).strip()
            if part:
                parts.append(part)
            buf = []
        else:
            buf.append(ch)
    part = "".join(buf).strip()
    if part:
        parts.append(part)
    return parts


arms: list[str] = []
for name in files:
    module = name[:-3]
    text = (root / name).read_text(encoding="utf-8")
    for match in pattern.finditer(text):
        fn, params = match.group(1), match.group(2)
        call_args: list[str] = []
        for raw in split_params(params):
            if raw.startswith("state:"):
                call_args.append("HostState::new(state)")
                continue
            pname = raw.split(":", 1)[0].strip()
            call_args.append(f'arg(args, "{pname}")?')
        joined = ", ".join(call_args)
        arms.append(f'        "{fn}" => run(crate::{module}::{fn}({joined})),')

body = """//! JSON entry points for every Tauri command. The WinUI host calls these by name.
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
"""
body += "\n".join(arms)
body += """
        _ => Ok(None),
    }
}
"""
(root / "bridge.rs").write_text(body, encoding="utf-8")
print("arms", len(arms))
