# Drawings (the ShilpiDB engine inside O-Studio)

Drawings are 2D vector drawings kept as geometry, not pictures, and linked to a project. The engine is the ShilpiDB codec and spatial grid (`shilpidb/`), used in-process by `office-core/src/drawings.rs`. A drawing is stored in the company file's `shilpi_drawings` table in ShilpiDB's `.vdb` layout, so the same bytes can be given to a `shilpid` server.

## Operations (office interface and the native bridge)
| Operation | Permission | Does |
|---|---|---|
| `drawing_import` `{project_id, name, path}` | Edit | Reads an ASCII DXF and stores it. Returns `{id, entities, layers, skipped, skipped_kinds}` |
| `drawing_list` `{project_id?}` | View | Lists drawings without geometry |
| `drawing_view` `{id, window?: [x0,y0,x1,y1]}` | View | Shapes of a drawing, or only those whose box meets the window (answered by the spatial grid). At most 20,000 per call; `truncated` says more exist |
| `drawing_at` `{id, x, y, tolerance}` | View | Shapes under a point |
| `drawing_delete` `{id}` | Edit | Removes the drawing |

## What the DXF reader knows
LINE, LWPOLYLINE, POLYLINE with VERTEX, CIRCLE, ARC, TEXT, MTEXT and POINT, on named layers, with units from `$INSUNITS`. Anything else (blocks, splines, hatches, dimensions) is counted and reported in `skipped_kinds`, never dropped silently. Binary DXF and DWG are not read; save as text DXF.

## Not built yet
DWG, PDF and SVG import, entity metadata and attributes, topology, embeddings and semantic search (the later phases of `shilpidb/ROADMAP.md`).

Tests: `office-core` (`drawings::tests`) and the native smoke test (`native/Adms.Smoke`).
