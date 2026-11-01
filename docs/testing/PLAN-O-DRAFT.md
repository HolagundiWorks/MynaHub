# Test plan: O-Draft

O-Draft is the 2D CAD (Rust engine, WinUI shell). Source of truth for what exists: `O-DRAFT-main\docs\status.md` (capability matrix, "Implemented" vs "Planned"). A feature listed under "Planned" is `N/A`, not a failure. Command reference: `docs/UI_UX.md`, `docs/input-forms.md`, `docs/hcw-kit-port.md`. Case format: [README.md](README.md). `[N]` normal, `[B]` boundary or bad input, `[R]` repeat or undo.

**How to drive it without the mouse:** every menu entry is a command. Use `aad-cli run --script x.scr` or the JSON control channel for the command itself (`takeoff.record`, `layer.set`, ...), and the WinUI window for what the command shows (dialogs, panels). A command-level pass does not replace a window-level pass for a feature that has a dialog: do both and record both.

Every command case also checks **undo and redo** (`[R]`) unless the command is marked no-undo (`takeoff.record`).


> **Project selection (2026-11-01).** The project is chosen and locked in the launcher; see [../PROJECT-SELECTION-DESIGN.md](../PROJECT-SELECTION-DESIGN.md). The app is started on a project with `OSTUDIO_PROJECT_ID` and `OSTUDIO_PROJECT_OSP` (a test folder with no `.osp` yet is fine: the first open makes it). Started with neither, the app asks the launcher and exits. Automation and tests start it with `OSTUDIO_STANDALONE=1`, `--control PORT` or a file on the command line instead. The cases below that name New, Open or Save as project are written for the locked flow.

## D1 Smoke

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| DRF-SMK-01 | Start | Start `ODraft.WinUi.exe` | Main window opens with welcome overlay; no crash events | UIA, event log |
| DRF-SMK-02 | New drawing | File, New | An empty drawing tab; model space; command line ready | UIA |
| DRF-SMK-03 | Draw and close | Draw a line, close without saving | Asks to save; Discard closes cleanly | UIA |
| DRF-SMK-04 | Engine suite | `cargo test --workspace` | 0 failed | test output |

## D2 Files

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| DRF-FIL-01 | Open DXF | Open the sample DXF | Opens; view fitted; entity count equals the file's (`drawing.inspect`) | command |
| DRF-FIL-02 | Open DWG | Open the demo DWG (native builds) | Opens through the DXF bridge; known unsupported objects reported, not silently dropped | message, inspect |
| DRF-FIL-03 | Save DXF round trip | Save, close, reopen | Same entity count, layers, blocks, text; `<file>.aadrev.json` written beside it (SHA-256, bytes, time, count) | file, `drawing.revision` |
| DRF-FIL-04 | Save DWG | Save as DWG, reopen | Round trips; what could not be kept is reported | message |
| DRF-FIL-05 | `.bak` | Save over an existing file | A `.bak` copy of the previous file exists | folder |
| DRF-FIL-06 | First open of a locked project | Start on a project folder with no `.osp` | One `.osp` is created with an empty drawing; `inspect` shows `drawing` rows; the tab is titled with the project file | `aad-odp inspect` |
| DRF-FIL-07 | No file or project commands | Open the File menu on a locked project | New, Open, Save As, New Project, Open Project are absent; Save, Close, Export, Print, History are present | UIA |
| DRF-FIL-08 | Save unchanged drawing `[R]` | Save an `.osp` twice with no change | Second save writes no new drawing revision (revision unchanged) | `inspect` |
| DRF-FIL-09 | Corrupt file `[B]` | Open a truncated DXF; open a zero-byte file; open a `.txt` renamed `.dxf` | Error message; the app stays usable; no crash | event log |
| DRF-FIL-10 | Flatten on import | Open a DXF with 3D elevations | Flattened to 2D; report lists what was flattened | message |
| DRF-FIL-11 | Export PDF / SVG / PNG | Export each from a layout | Files created; PDF has layers; PNG at the chosen resolution; open in a viewer | files |
| DRF-FIL-12 | Export CSV / data extraction | `export csv`, `dataextraction`, `eattext` | Tables correct for a known drawing | files |
| DRF-FIL-13 | Insert file | Insert another drawing as block/geometry | Placed at the chosen point | inspect |
| DRF-FIL-14 | Recent files | Open three files | Listed in File menu; a deleted file shows as missing or is skipped | UIA |
| DRF-FIL-15 | Several drawings open | Open 3 tabs; switch; close the middle | Active drawing correct; no cross-edits | UIA |
| DRF-FIL-16 | Save off the UI thread | Save a large drawing | Window stays responsive during the save; result committed | timing |
| DRF-FIL-17 | File association | Double-click an `.osp` in Explorer | Opens in O-Draft (suite installer registers `.osp`) | Explorer |
| DRF-FIL-18 | Drawing compare | `dwgcompare` against itself and against a changed file | Equal vs different | command |

## D3 Drawing and modify

Run each command with a normal case, a bad input, then undo and redo.

| ID | Feature | Expected (summary) |
|---|---|---|
| DRF-DRW-01 | Line, polyline, circle, arc, ellipse, rectangle, polygon | Correct geometry for typed coordinates; endpoints equal the input; cancelling with Esc leaves no entity |
| DRF-DRW-02 | Spline, point, construction line, ray, multiline | Created; properties panel shows type |
| DRF-DRW-03 | Revision cloud, wipeout | Created; wipeout masks what is under it in the plot |
| DRF-DRW-04 | Hatch (Hatch Manager, pattern, scale, angle), boundary, region | Hatch fills the picked boundary; changing scale/angle updates; huge hatch density becomes a tint instead of slowing the view |
| DRF-DRW-05 | Text, multiline text, edit by double-click | Text height/style obeyed; edit round trips; unicode preserved |
| DRF-DRW-06 | Blocks: make, insert, redefine, edit (`bedit`, `bsave`, `bclose`, `refedit`) | Insert count and scale correct; redefine updates all inserts; the editor returns to the drawing intact |
| DRF-MOD-01 | Move, copy, rotate, scale, mirror | Results equal the geometric expectation (check coordinates) |
| DRF-MOD-02 | Stretch, offset (open, closed, spline), trim, extend, fillet, chamfer, break, join, explode, lengthen, align | Correct results; bad selection gives a prompt message, not a crash |
| DRF-MOD-03 | Array rectangular, polar, path | Count and spacing correct; zero rows/columns refused |
| DRF-MOD-04 | Overkill, polyline and spline editing, group and ungroup (`group.explode`) | Duplicates removed; group moves as one |
| DRF-MOD-05 | Selection: click, window (left to right inside), crossing (right to left), Shift remove, select all, grips | Selection sets match the rule; grip edit changes the entity |
| DRF-MOD-06 | Clipboard: copy, cut, paste, paste as block, copy with base point | Pasted at the pointer; base point honoured |
| DRF-MOD-07 | Undo/redo depth | 50 undo steps then 50 redo restore the exact state (`drawing.inspect` hash) |
| DRF-MOD-08 | Non-finite and huge input `[B]` | `1e308`, `NaN`, `-0`: refused with a message |

## D4 Dimensions, annotation, layers and properties

| ID | Feature | Expected |
|---|---|---|
| DRF-DIM-01 | Linear, aligned, radius, diameter, angular, baseline, continue, quick dimension | Measured value equals the geometry to the displayed precision |
| DRF-DIM-02 | Multileader, dimension styles, text edit | Style change updates dimensions; edit keeps association |
| DRF-DIM-03 | Associativity | Move a dimensioned line: the dimension follows |
| DRF-DIM-04 | Units and annotation | Change drawing/display units, plot scale: text and dimension sizes follow; `insunits` read/set; unit-bearing fields accept `12mm`, `0.3m`, `1'6"`, `18"` |
| DRF-LAY-01 | Layer manager | Create, rename, colour, linetype, lineweight, transparency, on/off, freeze, lock, delete; deleting a layer in use refused |
| DRF-LAY-02 | Layer tools | Layer states, `laytrans`, `laywalk`, merge, delete, layer scope; the current-layer box in the top bar changes the current layer |
| DRF-LAY-03 | Properties panel | Select an entity: properties shown and editable; multiple selection shows "varies" |
| DRF-LAY-04 | Linetypes, lineweights, plot styles, text and dimension styles | Each applied entity looks as set; metric linetypes available |
| DRF-LAY-05 | Drawing spaces and layer protection | Save a layer-state space, change layers, recover it; a locked layer refuses edits with a message; modified marker shown |
| DRF-LAY-06 | Colour dialog | **Select Color** with the ACI palette, true colour and `.acb` colour books | Choosing a colour sets entities; true colour round trips through DXF/`.osp`; opening a damaged `.acb` gives a message |
| DRF-LAY-07 | Architectural layer sets | `hcwlayers` (standard, permit, presentation, both, all) | The expected layers exist with the expected properties |
| DRF-LAY-08 | Purge, audit, recover, flatten | `PURGE`, `AUDIT`, `RECOVER`, `FLATTEN` on a damaged sample | Reports match the damage; clean drawing reports clean |

## D5 Layouts, plotting and output

| ID | Feature | Expected |
|---|---|---|
| DRF-PLT-01 | Layouts | New layout, list, rename, delete; tabs along the bottom |
| DRF-PLT-02 | Viewports | Add, scale, clip (`vpclip`), lock; model changes show in the viewport |
| DRF-PLT-03 | Page setup, plot window | Print/Export/plot preview ask for a window, fit Nameplate L or P |
| DRF-PLT-04 | PDF plot | Output is a valid PDF at the stated paper size; lineweights and colours as set; text selectable |
| DRF-PLT-05 | Plot off the UI thread | The window stays responsive |
| DRF-PLT-06 | Plot of an empty drawing `[B]` | A message, not a blank crash |

## D6 Architecture tools

| ID | Feature | Expected |
|---|---|---|
| DRF-ARC-01 | Rooms: `room`, `rarea`, room label settings | Label shows name and area equal to the polygon area in the chosen unit; the floor comes from the top bar |
| DRF-ARC-02 | Text tools: `fixtxt`, `fixtxth`, `txtalign`, `txtdup`, `txtaudit`, `txtexport`, `txtinc` | Each does what the command help says; `txtexport` CSV rows equal the text count; `txtinc` counts up |
| DRF-ARC-03 | Walls: `wall.draw` (mitred faces), `wall.door`, `wall.window` | Wall faces and caps correct at joins; an opening cuts the wall and takes its width from the block |
| DRF-ARC-04 | Structure: `grid.axis`, `column.place`, `column.schedule`, `lintel.draw`, `stair.plan`, `lift.shaft`, `rail.draw` | Stair risers and goings satisfy the entered height; concrete volume equals hand calculation; column schedule matches placed columns |
| DRF-ARC-05 | Levels and symbols: `level.mark`, `level.schedule`, `section.marker`, `elevation.marker`, `north.arrow`, `slope.arrow` | Symbols placed with correct values |
| DRF-ARC-06 | Project data: `project`, `floors`, `beams`, level tabs | Floor names and heights saved in the drawing; default slab and beam values used by tools |
| DRF-ARC-07 | Project wizard | File, Project Wizard: five pages; one undo step; editing an existing project reloads values |
| DRF-ARC-08 | Levels | `level.new/set/delete/activate/moveto/fromfloors`, tab bar (activate, trace mark, context menu: settings, trace, milestone, history, delete) | Active level shows; others faded traces; DXF/DWG round trip keeps levels |
| DRF-ARC-09 | Sheet sets | `sheetset.new/set/delete/activate/next/prev`, `sheetset.standard` (five standard tabs) | A sheet view edits the set's annotation and shows the level faded and read-only; new objects go on the set's layers |
| DRF-ARC-10 | Layout viewports by level | Viewport shows a chosen level with optional annotation and traces |
| DRF-ARC-11 | Level migration | `level.import`, `level.export`, `project.merge` | Units checked; handles fresh; associations kept |
| DRF-ARC-12 | Xrefs and floor projects | `xref.attach/reload/detach/bind/list`; `project.setup/open`, `floor.*` | Attach shows content; reload picks up changes; bind makes it local; detach removes; circular xref refused |
| DRF-ARC-13 | Standard blocks, openings | `openings.std`, `openings.sync`, `openings.deduct`, `openings.table` | Door/window schedule table equals the placed openings |
| DRF-ARC-14 | Smart blocks | `smartblock.list/insert/get/edit` (form) | Parameters edit the instance; schedule values follow |
| DRF-ARC-15 | Parametric walls | `param.graph` and wall commit | Formula, units, cycle detection; a cycle is refused with a message; committed wall equals the evaluated geometry |
| DRF-ARC-16 | Building elements and Indian code index | `classify.set/get/clear`, `spec.list/get/search` | Classification stamped; search returns expected ids |
| DRF-ARC-17 | Electrical | `electrical.blocks/number/autonumber/layers/connect` | Numbering unique and sequential; switchboard wire table matches the connections; autonumber on insert |
| DRF-ARC-18 | Calculator and Pomodoro widgets | Open, use, close | Arithmetic correct (`12'6" + 8"`); timer counts down; widgets stay on top and close |
| DRF-ARC-19 | Companion / Dynamo link | If installed | Graph manager lists graphs; preview overlay appears; N/A if not installed |

## D7 Take-off

Take-off tools show in the top strip only while **Take-off** is on (toggle in the bottom bar, next to Nameplate).

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| DRF-TKO-01 | Toggle | Toggle Take-off off/on | Take-off tools (Ext wall, Int wall, Beam, Floor, Ceiling, Column, Deduct, Paint, Openings, Record, Map layers) hidden/shown | UIA |
| DRF-TKO-02 | Start and draw | `takeoff.start`, draw a wall line on `MEASURE-FULLBRICK`, `takeoff.brick` | Wall length and area correct in mm; table drawn | inspect |
| DRF-TKO-03 | Units | Same 10 ft wall in inches and in mm | Both report 3048 mm | record |
| DRF-TKO-04 | Columns, beams, slabs, lintels | Measure each | Quantities equal hand calculation; L-shaped slab is flagged as not rectangular | inspect |
| DRF-TKO-05 | Openings and deduct | Place doors/windows on a wall | Wall area reduced by the opening (ignore-small rule documented in CoreOp) | numbers |
| DRF-TKO-06 | Rooms and floors | Room label, floor regions | Floors stamped on measured walls | record |
| DRF-TKO-07 | Phases | `takeoff.phases`, `takeoff.phase` | Phase list; changing phase updates marked dimensions | command |
| DRF-TKO-08 | Map layers | `takeoff.maplayers` | Legacy layers map to marks | command |
| DRF-TKO-09 | Annotated drawing | `takeoff.annotate` | PDF with the measured items marked and a legend with counts | PDF |
| DRF-TKO-10 | Record | Save as `.osp`, `takeoff.record` | Header and one record per item; `inspect` shows headers/rows; second record with no change writes nothing (`unchanged`) | `aad-odp inspect` |
| DRF-TKO-11 | Record without a project `[B]` | `takeoff.record` on a `.dxf` | Error telling the user to save the drawing as a project (`.osp`) | message |
| DRF-TKO-12 | Record after removing an item `[R]` | Erase a wall, record again | That record is deleted (`deleted: 1`) | inspect |
| DRF-TKO-13 | Two drawings, one project | Record two drawings into one `.osp` | Different slots; neither touches the other; a warning if both measure the same floor | inspect |
| DRF-TKO-14 | Restore tables | `takeoff.restore` | Tables redrawn from the project file | command |
| DRF-TKO-15 | Quantity trust | Run `crates/engine/tests/quantity_trust.rs` fixture drawing | Known wall/opening numbers match | test |
| DRF-TKO-16 | Bridge export/import | `bridge.export` and `bridge.import` | Project, floors, beam depths, schedule, rooms round trip | files |
| DRF-TKO-17 | PDF as geometry and underlay | `pdf.import`, `underlay.attach/calibrate/list/remove` | Lines/text imported; calibration with two points and a known distance scales correctly | measure |

## D8 History, revisions and project file

| ID | Feature | Expected |
|---|---|---|
| DRF-HIS-01 | Save milestone | A named milestone is listed with time and author |
| DRF-HIS-02 | Restore | Restore returns the drawing to the milestone; later changes undoable |
| DRF-HIS-03 | Automatic restore points and pruning | Points appear; pruning keeps the recent and daily ones |
| DRF-HIS-04 | Storage | For an `.osp` the history is in the file (`history_record`), otherwise in `<drawing>.history.osp`; saving history does not change `meta.revision` |
| DRF-HIS-05 | Damaged history file `[B]` | A garbage `.history.osp` gives an error, not a crash |
| DRF-HIS-06 | Revision register | `revision.issue/list/set/void/table/codes/clouds`: issue updates the title block `REV` and draws the table; void keeps the number |
| DRF-HIS-07 | Drawing revision | `drawing.revision` reports current / stale / missing for changed files |
| DRF-PRJ-01 | `.osp` concurrency | Open the same `.osp` in O-Draft and O-CoreOp; save in each alternately | Revisions increase by one per save; no data lost; neither app shows an error |
| DRF-PRJ-02 | Active-project pointer | Start on a locked project | `%LOCALAPPDATA%\O-Studio\active-project.json` names the project file; closing the app deletes it | file read |
| DRF-PRJ-03 | Company file details | `companyfile` command / project details | Company and project details shown read-only from the company file; nothing written back |

## D9 Scripting, automation and AI tools

| ID | Feature | Expected |
|---|---|---|
| DRF-AUT-01 | Scripts | `script x.scr`, `macro`, `lisp` subset (arithmetic, `setq`, `princ`, `getvar`/`setvar`, `command`) give the documented results; an unsupported LISP form gives an error |
| DRF-AUT-02 | Control channel | `127.0.0.1` only; a command returns JSON; a line above 1 MiB is refused; a non-loopback bind is refused without opt-in |
| DRF-AUT-03 | MCP server | Tools listed; a read tool works; a changing tool honours confirmation rules |
| DRF-AUT-04 | `aad-cli` | `run --script`, `--version`, auditor commands work |
| DRF-AUT-05 | Agent tools | Allow-listed tools only (flatten, audit, purge, recover, summary, units, plot configuration, annotation layout, spatial queries); a tool outside the list is refused; ShilpiDB tools no longer exist |
| DRF-AUT-06 | Auditor | `audit_drawing`, `validate_drawing`, `check_command_parity`, `check_command_menu_coverage`, `check_ui_tokens` report; fixing needs confirmation |
| DRF-AUT-07 | Floating AI bar / assistant | With the AI host up: ask about the drawing; answer uses digest/cards; with the host down: clear message |
| DRF-AUT-08 | Preview edit | `proposeedit` describes, `previewedit` runs and restores | State identical afterwards |

## D10 Shell, performance and general

| ID | Feature | Steps | Expected | How to check |
|---|---|---|---|---|
| DRF-SHL-01 | Layout | Menus: File, Edit, View, Insert, Format, Tools, Draw, Dimension, Modify, Architecture, Window, Help | All present; each entry runs or is disabled with a reason | UIA |
| DRF-SHL-02 | Toolbars | Left Draw/Modify/Annotate; top strip (layer box, architecture tools, gear) | Present; "Customize Top Bar" works; `Ctrl+Shift+F11` resets | UIA |
| DRF-SHL-03 | Side panels | Layers, Properties, Blocks stack on the right; pin and auto-collapse | Panels open, float, close, remember state | UIA |
| DRF-SHL-04 | Command line | Type `line`, aliases, F2 history; prompts; Esc, Enter, right-click behaviours | As documented in UI_UX.md | UIA |
| DRF-SHL-05 | Status bar | Snap, ortho, polar, tracking, dynamic input, coordinates, units popup | Each toggle changes behaviour (F3, F8, F10, F11, F12) | UIA |
| DRF-SHL-06 | Object snaps | Each snap mode; Tab cycles; snap markers | Point equals the geometric snap to 1e-6 | command |
| DRF-SHL-07 | Mouse | Wheel zoom about pointer, middle-drag pan, window/crossing selection | As documented | UIA |
| DRF-SHL-08 | Keyboard shortcuts | The list in UI_UX.md; shortcuts do not fire in a text field | Each works; `F4`, `F5`, `F7`, `F9` do nothing as documented | UIA |
| DRF-SHL-09 | Themes | Light (white drawing area) and dark (black) | Both readable; theme toggle re-applies to open tool windows | screenshots |
| DRF-SHL-10 | Window sizing | 1280x720, 1024 wide | Secondary windows size to content; none off-screen | screenshots |
| DRF-SHL-11 | Performance | Open the demo drawing; pan/zoom 30 s; run with `AAD_PERF=1` | No frame over 100 ms in a steady pan; log in `%LOCALAPPDATA%\OSAS\odraft-perf.log`; memory stable | perf log |
| DRF-SHL-12 | Large drawing `[B]` | 100,000 entities | Opens; pans; no crash; times recorded | timing |
| DRF-SHL-13 | Update check | Help, check for updates | Message; no crash | UIA |
| DRF-SHL-14 | About, fonts, typography | About window; View, Font Preset (5 presets) | Opens; preset changes UI fonts | UIA |
| DRF-SHL-15 | Web build | `cargo check --target wasm32-unknown-unknown` for engine and `aad-odp` | Compiles; project file features absent by design | build |
| DRF-SHL-16 | Crash recovery | Kill the process mid-edit, reopen | The last saved file intact; history/restore points available | files |
