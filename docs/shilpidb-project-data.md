# Project data from ShilpiDB

The company has one database file. Each project has its drawing (`.dwg`) and a ShilpiDB file. The project's running billing, estimate, tasks, quantities and purchase orders are read from that ShilpiDB file; O-Studio only reads it.

## In O-Studio

Open a project (Practice, Projects). The **ShilpiDB project data** section shows the linked file and the figures read from it. Choose the file with **Choose ShilpiDB file**, or paste its full path and press **Use this path**. **Reload** reads it again, **Unlink** removes the link. The company file keeps only the path (`projects.shilpi_path`, migration 0036); the figures are read each time and never copied.

## The file layout

The file is an ordinary ShilpiDB data file (the layout `shilpid` saves). Business records are records whose payload starts with the tag `BIZ1` and a kind byte; they have an empty bounding box, so they never appear in a spatial query. Records without the tag (drawing geometry) are ignored by O-Studio and kept when the file is rewritten.

| Kind | Record | Fields |
|---|---|---|
| 1 | Estimate item | code, description, unit, quantity, rate (amount = quantity x rate) |
| 2 | Billing stage | stage, description, percent, amount, status (`pending`, `invoiced`, `paid`), date |
| 3 | Task | title, assignee, due, status (`open`, `doing`, `done`), percent |
| 4 | Quantity | item, description, unit, quantity, source |
| 5 | Purchase order | number, vendor, date, description, amount, status |

Strings are length-prefixed UTF-8 and numbers are little-endian, as in the `shilpidb` codec. A kind this version does not know is skipped, so an older reader still opens a newer file.

## Where the code is

- `shilpidb/crates/shilpi-project`: the records, `read_file` and `write_file`, and `examples/sample.rs`, which writes a sample project file (`cargo run --example sample -- <path>`).
- `office-core/src/project_shilpi.rs`: the link, the read and the totals. Engine operations `project_shilpi_get`, `project_shilpi_set` (needs edit permission) and `project_shilpi_read`.
- `native/Adms.Desktop/ShilpiProjectPanel.cs`: the section on the project page.

## Not done

- Whatever writes these records into a project's file (O-Draft, a takeoff tool) is not built here; the layout above is what it must write.
- The company file's own stages, estimates and tasks still exist and are still shown on their pages; they are not yet removed for projects that have a ShilpiDB file.
- The file chooser button could not be driven by the test harness, so it was checked only through the paste-the-path route.
