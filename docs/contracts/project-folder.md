# Contract: suite project folder (version 1)

When O-Studio creates a project it also creates an on-disk workspace so O-Draft and O-Form share one root with the `.osp` project file.

## Layout

```text
projects/<slug>-<project_id>/
  osas.project.json
  project.osp          # created on first save by O-Draft or O-CoreOp
  draft/model.dxf
  draft/project.json
  form/model.blend
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
    "project": "project.osp",
    "draft": "draft/model.dxf",
    "form": "form/model.blend"
  },
  "apps": {
    "odraft": { "file": "project.osp", "drawing": "draft/model.dxf" },
    "ocoreop": { "file": "project.osp" }
  }
}
```

## Operations

| Op | App | Does |
|----|-----|------|
| `project_save` (create) | O-Studio | Inserts row + scaffolds folder |
| `project_workspace_ensure` | O-Studio | Re-scaffolds / repairs |
| `project_workspace_get` | O-Studio | Returns paths + drawing_id |
| Open Project Folder | O-Form | Binds manifest |
| `project.open` / open DXF | O-Draft | Uses `draft/` and `project.osp` |

## Code

`office-core::project_workspace` — `scaffold`, `get`.
