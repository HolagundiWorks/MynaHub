"""Regenerate bridge.rs match arms from HostState entry points.

Usage (from src-tauri): python3 tools/gen_bridge.py
Then merge the printed arms into src/bridge.rs (or replace the match body).
"""
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

pattern = re.compile(
    r"pub fn (\w+)\(\s*state:\s*HostState<'_>(.*?)\)\s*->",
    re.S,
)


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
        fn, rest = match.group(1), match.group(2)
        call_args: list[str] = ["HostState::new(state)"]
        for raw in split_params(rest):
            if not raw:
                continue
            pname = raw.split(":", 1)[0].strip()
            call_args.append(f'arg(args, "{pname}")?')
        joined = ", ".join(call_args)
        arms.append(f'        "{fn}" => run(crate::{module}::{fn}({joined})),')

print("\n".join(arms))
print("arms", len(arms), file=__import__("sys").stderr)
