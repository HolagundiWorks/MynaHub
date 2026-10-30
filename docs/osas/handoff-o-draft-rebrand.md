# Hand-off: bringing the drafting app into OSAS as O-Draft

> **Status 2026-10-19: mostly done on `O-DRAFT` `main`.** Already there: the product name in the packaging, About, splash and EULA (the last strings were fixed in `1f3f7dc`), the black accent in both palettes with contrast tests in `theme.rs`, the new mark and its arc rasteriser in `xtask/src/logo.rs`, `o-draft.exe` and `ODraft.WinUi.exe`, the installer names, and the OSAS launcher looking for `o-draft.exe` with `aad.exe` as the older fallback.
> **Still open:** (1) `cargo xtask ci` and a real build of the shell were not run after the last change, and one command-bar test (`space_runs_the_first_command_in_the_list_and_a_sentence_keeps_its_spaces`) failed in `ui-egui` on `origin/main`; it was not checked whether it fails without the rebrand. (2) O-Draft was not installed, so the OSAS Open O-Draft card was never exercised. (3) The hub integration in section 11 below is not started. (4) The accessibility audit (section 7) was not run. (5) The developer working copy `AADT` has 37 uncommitted files of take-off work that are not part of this.

For whoever works in the O-Draft repository (`HolagundiWorks/O-DRAFT`). Written 2026-10-18. Nothing in the O-Draft repository has been changed yet. I read it through GitHub only; it is not cloned on the machine this was written on, so I did not build or run it. Every file and line named below comes from the repository's own text, but run the searches in section 9 to find what I missed.

## 1. What is being asked
OSAS, the Open Source Architecture Suit, has three apps. Two are rebranded; this is the third.

| App | Accent | State |
|---|---|---|
| **O-Studio** | orange `#FF6B00` | Done, in `HolagundiWorks/O-STUDIO` |
| **O-CoreOp** | blue `#1F5FD1` | Done, in `HolagundiWorks/O-COREOP` (merging branch `claude/aos-shell`) |
| **O-Draft** | black `#1B1C20` | **This hand-off** |

The decision, from the owner: all three use **the O-CoreOp logo**, redrawn in each app's colour. O-Draft's is the logo on a black plate. The suite launcher is `HolagundiWorks/OSAS-Open_Source_Architecture_Suit`; the shared design and the marks are in `HolagundiWorks/hcw-ui` (both private).

## 2. What changes, and what does not

**Changes** (display names and artwork):
- The product name in every place a person sees it: "O-Draft - Accelerated Architecture Drafting" becomes "O-Draft". The line under it can say "Drafting. Part of OSAS, the Open Source Architecture Suit."
- The logo and every icon generated from it.
- The accent colour, and the installer's product name.

**Does not change.** Other things read these, so renaming them breaks files, servers or users' settings:
- Crate and binary names: `aad`, `aad-cli`, `aad-host`, `aad_host.dll`, `Aad.WinUi.exe`, the `aad-*` crates. Say if the owner wants the executables renamed; that is a separate decision.
- Command ids, the command-line grammar, the MCP tool names, the control-channel methods.
- File formats and markers: the `AAD_PROJECT` extension data in DXF files, the **`.osp` project file**, the `hcw-aqc-bridge` file. Legacy `.vdb` take-off sidecars are retired. Existing drawings must keep opening.
- The `aad` session name in the AORMS sign-in (`app: "aad"`, credential entry `AORMS/Connect/aad`), the application id `io.github.holagundiworks.aad`, `%APPDATA%\AAD` and its `models` folder, environment variables such as `AAD_CONTROL_PORT`, `AAD_NO_AORMS`.
- The WiX **UpgradeCode** in `packaging/windows/aad.wxs`. Keep it, or an installed copy will not upgrade.
- The repository name.

## 3. The logo
The mark is the O-CoreOp logo: a half ring open to the right, a smaller half ring below the centre, and a small ring at the centre, all white on a plate. O-Draft's plate is `#17181B`. It replaces O-Draft's present logo (a blue square with a white "A" and a crosshair).

Ready-made files, drawn by `hcw-ui/tools/Brand`, in `hcw-ui/brand/draft/`: `mark.svg`, `mark.png` (1024), `mark-256.png`, `app.ico` (16 to 256), `lockup.png`, and `tiles/` (Windows tile sizes). Use the SVG as the vector source.

Geometry, in a 194 by 194 box, centred at (107, 96), all strokes white and flat-ended:

| Part | Shape | Radius (to the middle of the stroke) | Stroke |
|---|---|---|---|
| The C | left half of a circle, from the top (107, 27) round the left to the bottom (107, 165) | 69 | 18 |
| The lower arc | lower half of a circle, from the left (57, 96) round the bottom to the right (157, 96) | 50 | 12 |
| The ring | full circle | 18.5 | 5 |

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 194 194">
  <rect width="194" height="194" fill="#17181B"/>
  <g fill="none" stroke="#fff">
    <path d="M107 27A69 69 0 0 0 107 165" stroke-width="18"/>
    <path d="M57 96A50 50 0 0 0 157 96" stroke-width="12"/>
    <circle cx="107" cy="96" r="18.5" stroke-width="5"/>
  </g>
</svg>
```

**How O-Draft makes its icons.** `cargo xtask logo` (`xtask/src/logo.rs`) draws the logo itself, with no image tools: straight lines, its own small PNG writer, and the old artwork's 1254 by 1254 coordinates. It writes `assets/app-icon/aad-logo.svg`, `aad-1024.png`, `aad-256.png`, `aad-64.png`, `aad-macos-512.png`, `hicolor/<n>x<n>/apps/io.github.holagundiworks.aad.png`, `aad.ico` and `aad.icns`. The new mark has arcs, so `logo.rs` needs an arc rasteriser (an annulus segment: a pixel is inside when its distance from the centre is between the inner and outer radius, and its angle is in range; sample 4 by 4 per pixel for smooth edges). Scale from the 194 box to 1254 by 6.464. Keep the macOS icon's rounded corners and margin. Then update the `<title>` and the SVG, and run `cargo xtask logo`.

The `.ico` from `hcw-ui` can be used until the xtask draws the new mark, but the repository's rule is that everything under `assets/app-icon/` is generated, so the xtask must be updated before this is done.

## 4. Colour
O-Draft's accent is black. O-Draft's rules (`CLAUDE.md`): colours come from `theme::Tokens` in `crates/ui-egui/src/theme.rs`; there are two palettes, dark with a black drawing area and light with a white one; there is no other colour customisation. So the change is to the two token sets, not new settings.

Today `accent` is orange (`#F2841C` dark, `#D9660A` light) and `icon_accent` orange. Proposed:

| Token | Light palette | Dark palette | Why |
|---|---|---|---|
| `accent` | `#1B1C20` | `#F6F5F1` | Black on a dark panel is invisible, so the dark palette uses the light neutral |
| text on `accent` | `#FFFFFF` | `#17181B` | 4.5:1 or more in both |
| `icon_accent` | `#1B1C20` | `#F6F5F1` | follows `accent` |
| `selection` and the drawing selection blue | unchanged | unchanged | These mean "selected object"; they are not branding |

Check every new pair with `node tools/a11y/contrast.mjs` (in the `HolagundiWorks/O-STUDIO` repository, `tools/a11y/`): text 4.5:1, component edges and focus rings 3:1. The two palettes and their old values were read from `theme.rs` without a build, so confirm the token names before editing.

The WinUI shell (`apps/aad-winui`) has `App.xaml` with only `XamlControlsResources`, so it uses the system accent. Give it the same look: the `SuiteAccent.Black` colours, an amber-free primary button and the navigation look from `hcw-ui`. O-Draft is MIT OR Apache-2.0 and `Hcw.Ui` is now MIT licensed (see section 10), so it can be referenced directly.

## 5. Names: where to change them
From a text search of the repository. The first group is user-visible and must change.

| File | What |
|---|---|
| `README.md` | Title and first paragraph |
| `NOTICE`, `CONTRIBUTING.md` | The product name line |
| `apps/aad-winui/AboutWindow.cs` | The About text ("Accelerated Architecture Drafting") |
| `apps/aad-winui/Aad.WinUi.csproj` | Product and assembly title, if set |
| `apps/aad/src/egui_shell.rs` | The window title |
| `crates/ui-egui/src/dialogs.rs`, `chrome.rs` | The About label and the title with the version |
| `assets/app-icon/aad-logo.svg`, `xtask/src/logo.rs` | The SVG `<title>` |
| `packaging/windows/aad.wxs` | `SummaryInformation Description`, the product name, the Start menu shortcut, the install folder. **Keep the UpgradeCode** |
| `packaging/windows/package.ps1` | The name of the installer file |
| `docs/*.md` | The product name where it names the app. Technical wording only: O-Draft's rules forbid marketing text |

Install folder: make it `%LOCALAPPDATA%\Programs\O-Draft` (or the machine-wide equivalent). The OSAS launcher looks for `aad.exe` there first, then in `…\Programs\AAD`, then on the PATH.

## 6. Rules in O-Draft that apply to this work
- **Clean-room and assets.** Any file asset must be original or OSI-licensed and must have a row in `ATTRIBUTION.md`; `cargo xtask assets` enforces it. The O-Draft mark is Human Centric Works' own artwork, derived from the O-CoreOp logo. O-CoreOp's source code is AGPL-3.0 and must not be copied into O-Draft; the artwork is the owner's and may be used, but the `ATTRIBUTION.md` row should say who made it and under what licence it is given. The owner should confirm that wording.
- **Never crash.** No `unwrap`, `expect` or `panic` in non-test code. The arc rasteriser in `xtask` is tooling, but keep it panic-free anyway.
- **Quality gate.** `cargo xtask ci` before every commit (fmt, clippy `-D warnings`, tests, assets, layering, wasm).
- **Docs.** Update `docs/status.md` only for what exists. English only, technical wording.
- **Layering.** `cargo xtask layers`. Colours stay in the UI crate; nothing below it learns about the brand.

## 7. Accessibility to carry over
O-Draft has not been audited. The audit of O-Studio and O-CoreOp is in `O-STUDIO/docs/wcag-audit.md`. For O-Draft:
- Check the new accent pairs (section 4).
- Run `tools/a11y/uia-audit.ps1 -Exe <Aad.WinUi.exe> -Pages <menu items>` and `analyse.mjs` once the shell is rebuilt. Look for unnamed controls and targets under 24 px.
- O-Draft's own test matrix lists Tab and arrow traversal as open. Fill it in.
- The drawing is a GPU canvas with no accessibility tree. Say so in the docs, and point to the command line and the object list as the route for assistive technology.

## 8. Order of work
1. Names (section 5), without touching the "does not change" list.
2. Colour tokens (section 4), then check contrast.
3. Logo: the arc rasteriser in `xtask/src/logo.rs`, the SVG, `cargo xtask logo`, the `ATTRIBUTION.md` row, `cargo xtask assets`.
4. Installer (`aad.wxs`, `package.ps1`).
5. Run `cargo xtask ci`. Build the shell (`cargo build -p aad-host`, then `dotnet build apps/aad-winui/Aad.WinUi.csproj -c Debug -p:Platform=x64 -o target/aad-winui`). Start it with `--control PORT`, take a `ui.screenshot`, and look at the result.
6. Install it and check that OSAS (the launcher) finds it and opens it.
7. Run the accessibility audit and add its findings to `docs/wcag-audit.md` in the O-Studio repository.

## 9. Find what this document missed
```bash
git grep -n -i "Accelerated Architecture Drafting" 
git grep -n "O-Draft -\|O-Draft (" -- "*.md" "*.cs" "*.rs" "*.wxs" "*.ps1" "*.toml" "*.json"
git grep -n -i "orange\|0xf2.*0x84\|0xd9.*0x66" crates/ui-egui/src/theme.rs
git grep -n "aad-logo\|app-icon" -- ":!assets"
```

## 10. Decisions for the owner
Answered on 2026-10-18: (1) a light-neutral accent in the dark palette is acceptable; (2) the executables are renamed (see below); (4) the old "A with crosshair" logo is retired completely. Item 3 is settled: `hcw-ui` is MIT licensed, the marks have their own notice (`hcw-ui/brand/LICENSE.md`), so `Hcw.Ui` may now be added to O-Draft's WinUI shell as a dependency, and the `ATTRIBUTION.md` row can read "Original artwork by Human Centric Works, derived from the O-CoreOp logo; used under the owner's grant."

**Renaming the executables.** `aad.exe` becomes `o-draft.exe` (and `Aad.WinUi.exe` becomes `ODraft.WinUi.exe`). The cargo crates and library names stay, because other crates import them. Change the binary name in `apps/aad/Cargo.toml` (`[[bin]] name`), the WiX file and installer script, the WinUI project `AssemblyName`, and every place that starts the shell. The OSAS launcher looks for `aad.exe` today (`Osas.Core/AppCatalog.cs`, `ExeName`); it must be changed to `o-draft.exe` in the same release, with `aad.exe` kept as a fallback for old installs. Keep the `aad` AORMS session name.

**Retired logo.** Delete the old artwork from `assets/app-icon/` and drawing code from `xtask/src/logo.rs`; do not keep it as an alternative.


## 11. Joining the hub (started 2026-10-19)
**Built on branch `feat/o-draft-company-file` of O-DRAFT** (not merged): the `aad-office` crate loads `office.dll`, signs in with `OSAS_TICKET`, and reads `document_context`; the `companyfile` command sets the drawing's project details from it and `bplttitleblock` prints them. Tested against the real library and in the engine (302 tests). **Not done:** a menu or panel to choose the project, an automatic refresh, other schedules and sheets reading the same details, and a run in the installed app (O-Draft is not installed here).
OSAS now chooses the company and signs in once. An app is started with `OSAS_COMPANY`, `OSAS_TICKET` and `OSAS_USER` in its environment; a ticket works once for 120 seconds and is redeemed through `office.dll` (`auth.login_ticket`). O-Draft would need: read those variables at start; open the company file through the same engine to read the `ProjectRef` of the project it draws for; record that `ProjectRef` in the drawing and in the take-off it writes to the `.osp`. The contract is `docs/contracts/refs.md` and `docs/suite-hub.md` in the O-STUDIO repository. O-Draft should not manage users or choose a company.
