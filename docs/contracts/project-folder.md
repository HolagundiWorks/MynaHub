# Contract: suite project folder (version 1)

When MynaHub creates a project it also creates an on-disk workspace so MynaCAD and MynaMod share one root with the `.osp` project file.

## Layout

```text
projects/<slug>-<project_id>/
  osas.project.json
  project.osp          # created on first save by MynaCAD or MynaOps
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
| `project_save` (create) | MynaHub | Inserts row + scaffolds folder |
| `project_workspace_ensure` | MynaHub | Re-scaffolds / repairs |
| `project_workspace_get` | MynaHub | Returns paths + drawing_id |
| Open Project Folder | MynaMod | Binds manifest |
| `project.open` / open DXF | MynaCAD | Uses `draft/` and `project.osp` |

## Code

`office-core::project_workspace` — `scaffold`, `get`.
