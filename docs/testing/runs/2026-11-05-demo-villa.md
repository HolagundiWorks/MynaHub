# Test run: features of the 1.3.2 suite on the Demo Villa, 2026-11-05

Agent: Claude (UI Automation against the installed apps; `aad-cli`; a C# reader of the project file)
Build under test: O-Suite-Setup-1.3.2.exe (MynaHub `master`, MynaOps `main`, MynaCAD `main`); demo data: [demo-villa](../demo-villa/README.md)

| Case | Result | Observed |
|---|---|---|
| DEMO-01 demo company | PASS | `Adms.TestSeed` made `TestCo-2026-11-05.osas` (client, project TC-01 Demo Villa, four role users); Myna then lists it as the remembered company ("Database: TestCo-2026-11-05.osas (868 KB), closed") |
| DEMO-02 demo drawing | PASS | 12 m x 9 m plan with grid, columns, 4 doors, 8 windows, 4 rooms and five structural details built by script; 463 entities |
| DEMO-03 take-off recorded | PASS | The project file holds 36 take-off rows (7 walls, 12 openings, 12 columns, 4 rooms, 1 slab), the annotated PDF, and the drawing revision |
| DEMO-04 structural details recorded | PASS | `Details()` of the project file returns 4 (column, beam section, footing section, beam elevation; the footing plan is not an element detail); each evaluates in MynaOps's reader on 6 to 10 layers |
| DRF-01 open the project in MynaCAD | PASS | Opens `project.osp` with plan, take-off tables and details; the Take-off toggle shows Ext wall, Int wall, Beam, Floor, Ceiling, Column, Deduct, Paint, Openings, Fix deducts, Col deduct, Sync take-off, Record, Map layers |
| DRF-02 RCC out of the Blocks library | PASS | Blocks panel groups: Structural (22), Plumbing (34), HVAC (19), Electrical (21), Interior (15); RCC appears only as this drawing's own blocks and as tools |
| DRF-03 structural tools on the tool strip | PASS | RCC column, Round column, RCC beam, Footing, Slab, Lintel present |
| DRF-04 Welcome screen | PASS | The canvas no longer covers it; logo and three buttons visible (fixed earlier in this run) |
| COP-01 open on the project | PASS | Started with Myna's project variables, the title is "MynaOps: Demo Villa" |
| COP-02 take-off page | PASS | "project.osp, complete, 36 rows, annotated drawing" |
| COP-03 details on the element page | PASS | Beams: the section sketch lists "RCC beam cross-section (230 x 450)" and draws it with bars, stirrup, dimensions and callouts |
| COP-04 calculator apart from Ask | PASS | `12*3.5+2` gives `42`; a question typed in the calculator gives "Not a calculation" and is not sent to the Ask bar |
| COP-05 suite palette | PASS | Dark neutrals `#1B1F28`, surface `#242A36`, Dusk accent, lockup on a light plate |
| LNC-01 launcher starts and shows its window | PASS | Self-contained launcher starts; the Company login window is visible on `--launch draft`; status file reports the AI agent ready |
| LNC-02 sign in and lock the project | NOT RUN | Needs the test password entered in Myna; MynaOps was started with the project variables Myna would set |

## Found while testing

- The seed leaves `projects\tc-01-1\project.osp` as a 4 KB placeholder that is not a project file ("no meta table"); MynaCAD and `aad-cli` refuse it. It was moved aside before the drawing was saved.
- `takeoff.draw` of an exterior wall as one closed polyline gives its runs the same handle, so `takeoff.record` skips three of four rows ("another row has the same number"). Drawing the four runs separately records all of them.
- MynaCAD shows the opened project as modified (`project.osp *`) with "2 of 453 objects flattened to 2D" in the log, although nothing was edited.
- `takeoff.record` warns "the drawing file has no matching saved revision" when run before the first save; running it on the opened project avoids it.
