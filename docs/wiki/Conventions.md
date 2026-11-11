# Conventions

- **Names.** Use MynaHub, MynaOps, MynaCAD, Myna3D and Myna in user text; keep old spellings in identifiers.
- **Secrets** are never committed or printed. MynaCAD and MynaOps never hold cloud keys.
- **Docs** separate implemented from planned and are updated in the same change. `docs/SUITE.md` has identical copies in MynaOps and MynaCAD.
- **Git.** `main` and `master` stay identical (push both). Add files by name; never `git add -A` or whole `installer/` folders.
