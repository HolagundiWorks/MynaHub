# Contract: suite project folder (version 1)

When O-Studio creates a project it also creates an on-disk workspace so O-Draft, O-Form and ShilpiDB share one root.

## Layout

```text
projects/<slug>-<project_id>/
  osas.project.json
  draft/model.dxf
  draft/project.json
  form/model.blend
  shilpi/project.vdb   # created when an app binds ShilpiDB
```

Root is `<company_file_parent>/projects/…`. Path is stored on `projects.workspace_path`.

## Manifest (`osas.project.json`)

```json
{
  "v": 1,
  "format": "osas-project",
  "name": "…",
  "code": "…",
  "project_ref": { "v": 1, "company_uuid": "…", "project_id": 7 },
  "drawing_id": "…",
  "project_office_id": "…",
  "files": {
    "draft": "draft/model.dxf",
    "form": "form/model.blend",
    "shilpi": "shilpi/project.vdb"
  },
  "sync": {
    "mode": "live",
    "shilpid": "127.0.0.1:7420",
    "gateway": "127.0.0.1:7421",
    "poll_ms": 1500,
    "data_rel": "shilpi/project.vdb"
  }
}
```

## Operations

| Op | App | Does |
|----|-----|------|
| `project_save` (create) | O-Studio | Inserts row + scaffolds folder |
| `project_workspace_ensure` | O-Studio | Re-scaffolds / repairs |
| `project_workspace_get` | O-Studio | Returns paths + drawing_id |
| Open Project Folder | O-Form | Binds manifest, live sync |
| `project.open` / open DXF | O-Draft | Uses `draft/` |

## Code

`office-core::project_workspace` — `scaffold`, `get`.
