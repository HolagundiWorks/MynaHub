import re
import pathlib

root = pathlib.Path(__file__).resolve().parents[1] / "native" / "Adms.Desktop"
files = ["Workflows.cs", "ReportsPage.cs", "InvoicesPage.cs"]
rep = "\ufffd"

for name in files:
    p = root / name
    t = p.read_text(encoding="utf-8")
    before = t.count(rep)
    t = t.replace(f"  {rep}  ", "  \u00b7  ")
    t = t.replace(f" {rep} ", " \u00b7 ")
    t = re.sub(rf"([a-zA-Z0-9']){rep}(?=[\");,\s}}])", r"\1\u2026", t)
    t = t.replace(f'+ "{rep}"', '+ "\u2026"')
    t = t.replace(f". {rep}{{narration}}{rep}", '. "{narration}"')
    after = t.count(rep)
    p.write_text(t, encoding="utf-8", newline="\n")
    print(f"{name}: fixed {before - after}, remaining {after}")
