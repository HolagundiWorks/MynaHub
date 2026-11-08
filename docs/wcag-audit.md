# WCAG 2.2 AA audit of the OSAS apps

Audit of **Kivo**, **Falcon** and **Tren** on 2026-10-18, against WCAG 2.2 level AA. Kivo and Falcon were run and measured. Tren was not run, because it is not installed on this computer, so its section is a review of its documentation and is marked as such.

## What was checked, and what was not

| Method | Kivo | Falcon | Tren |
|---|---|---|---|
| Colour contrast, calculated from the colour tokens (`tools/a11y/contrast.mjs`) | Yes | Yes | Palette only |
| UI Automation walk of the running app: every element's role, name, keyboard focusability, size (`tools/a11y/uia-audit.ps1`, `analyse.mjs`) | 26 pages, 1,765 elements | 25 pages, 2,901 elements | Not run |
| Narrow-window screenshots (reflow) | Yes | Yes | No |
| Source scan for headings, names and live regions | Yes | Yes | Documentation only |
| Screen reader (Narrator, NVDA) | **Not done** | **Not done** | Not done |
| Keyboard Tab order and traps, by hand | **Not done** | **Not done** | Not done |
| 200% text size, 400% zoom, high contrast mode, voice access | **Not done** | **Not done** | Not done |

A clean automated result does not mean the app passes. Automated checks find missing names and small targets; they cannot judge whether a page makes sense to a screen reader user. The "not done" rows are the next step, and they are marked as such in the tables below.

## Summary

| | Kivo | Falcon | Tren |
|---|---|---|---|
| Interactive controls with no name | 0 of ~1,000 | **150+ found, now 0** | Not checked |
| Controls the keyboard cannot reach | 0 (system caption buttons excluded) | 0 (same) | Not checked |
| Text contrast | Pass | Pass | Not checked |
| Reflow at narrow widths | Window cannot be narrowed below about 960 px | **Fails** | Not checked |
| Headings | Yes, in the shared page template | **None inside pages** | Not checked |

## Fixed during the audit

| # | App | Criterion | Finding | Fix |
|---|---|---|---|---|
| F1 | Falcon | 4.1.2 Name, role, value | 67 drop-down cells and 44 text cells in the entry sheets, the calculator button, 13 layer toggles, the floor drop-down, the layers button and the letter text box had no name. A screen reader would announce "edit" or "button" | Each cell is named by its column and row ("Breadth b, row 3"), the others by their purpose. Re-run: 0 unnamed controls on 25 pages |
| F2 | Falcon | 2.5.8 Target size (24 px) | Layer toggles were 24 px, below the limit once the shell's 0.96 scale is applied. The All and None links were 21 px high | Toggles 28 px, links at least 28 px high |
| F3 | Kivo, Falcon | 1.1.1 Non-text content | The logo in the Kivo pane had no name. Falcon's navigation icons repeat the label beside them | The logo is named. The icons are hidden from assistive technology |
| F4 | Falcon | 1.3.1, 2.4.6 | No headings at all in the app | The page title line is now a level 1 heading. Headings inside pages are still missing (O4) |
| F5 | Kivo | 1.4.3 Contrast | Ink text on the pressed orange button measured 4.11:1 | The orange hover and pressed colours are lighter: 5.6 and 5.1:1 |

## Open findings

| # | App | Criterion | Finding | Severity |
|---|---|---|---|---|
| O1 | Falcon | 1.4.10 Reflow | At a 520 px wide window the page title ran into the Generate button, the tabs were cut off, the entry sheet disappeared and the status strip was clipped. **Fixed:** the pane is a menu button below 900 px and hidden below 560 px; headings and toolbars wrap; the sketch column gives its space back under 700 px. Checked at 520, 760 and 1300 px | Fixed |
| O2 | Falcon | 2.4.11 Focus not obscured | The floating clock and calculator covered the bottom right of the page. **Fixed:** the clock is in the navigation pane (shown when it is fully open); the calculator and notices are rows above the status strip | Fixed |
| O3 | Kivo | 1.4.10 Reflow | The window would not go narrower than about 960 px. **Fixed:** the minimum is 480 px, the navigation pane becomes a menu button below 900 px and hides below 560 px, and the page starts below the button. All 31 pages were opened at 520 px and looked at or measured: none loses content or needs sideways scrolling. Tables in the narrowest columns break words (for example the project name on Projects); that is cosmetic and listed in the roadmap. The header row on the list pages that was narrower than its rows is fixed | Fixed |
| O4 | Falcon | 1.3.1, 2.4.6 | Pages had no headings of their own. **Fixed:** the page title style is a level 2 heading and the section header style a level 3, under the level 1 page line in the shell | Fixed |
| O5 | Both | 4.1.3 Status messages | Success notices and Falcon's toast had no live setting. **Fixed in code:** every notice in Kivo is a `LiveNotice` (polite), errors stay assertive, and Falcon's notice host is polite. Not verified with a screen reader | Fixed in code, unverified |
| O6 | Kivo | 3.3.1 Error identification | The field at fault was not marked. **Mostly fixed:** the required-field and choice checks (record pages, transmittal log, time book, letters) use `Flow.Fault`, and on the record pages an error the engine returns is matched to the field whose label it names (for example "Enter the name." marks Name) with `Flow.Mark`. The field is outlined in the error colour in every state, its help text carries the message, and the keyboard moves to it. The password boxes (company-file sign-in, confirm password, your password) are marked too. Still message only: engine errors on pages that are not record pages | Mostly fixed |
| O7 | Falcon | 2.5.8, 2.1.1 | The Scale tool in the Drawing takeoff toolbar showed 4 px because the toolbar scrolled sideways. **Fixed:** the toolbar wraps | Fixed |
| O8 | Falcon | 1.4.4 Resize text | The shell was drawn at 0.96 scale. **Fixed:** it is 1.0 now, so text and targets follow the system settings. Text size at 200% not tested | Fixed |
| O9 | Kivo | 2.5.8 | The title bar's caption buttons are 46 by 23 px. They are supplied by Windows, so this is probably exempt | Informational |

## Found while re-testing (2026-10-19)

| # | App | Finding | State |
|---|---|---|---|
| N1 | Kivo | The first version of the polite notices subclassed `InfoBar`. WinUI does not support that: every page with a notice (Time, Enquiries and others) closed the app when opened. It was in the commit pushed the day before. Notices now come from `Flow.Notice()`, a plain `InfoBar` with the live region set | Fixed |
| N2 | Kivo | A new form on Salaries and some other Books pages asked "Discard unsaved changes?" when left, though nothing was typed: the lists that fill the form's drop-downs counted as edits. Only a field the person has focused now counts | Fixed |

## Criteria, by app

Status is **Pass** (measured or seen), **Likely** (the mechanism is in place but not tested end to end), **Fail**, or **Untested**.

| Criterion | Kivo | Falcon | Tren (documentation only) |
|---|---|---|---|
| 1.1.1 Non-text content | Pass | Pass after F3 | Untested. Docs: controls are named; the drawing canvas has no text alternative, which is inherent to a CAD canvas |
| 1.3.1 Info and relationships | Pass (headings, named table cells) | Fail inside pages (O4) | Untested |
| 1.4.3 Contrast (text) | Pass, lowest 5.1:1 | Pass, lowest 5.8:1 | Untested |
| 1.4.4 Resize text | Untested | Untested (O8) | Untested |
| 1.4.10 Reflow | Pass after O3 fix (all 31 pages checked) | Pass after O1 fix | Untested |
| 1.4.11 Non-text contrast | Pass. Button edges 3:1 or more; field borders 3.6:1 | Pass | Untested |
| 1.4.12 Text spacing | Untested | Untested | Untested |
| 2.1.1 Keyboard | Likely. Every control is focusable; no keyboard-only walk done | Likely; O7 fixed | Docs: keyboard drafting path exists (arrow keys, command line); Tab traversal listed as open in its own test matrix |
| 2.4.3 Focus order | Untested | Untested | Open in its test matrix |
| 2.4.7 Focus visible | Pass (visible on the navigation pane) | Pass (same) | Untested |
| 2.4.11 Focus not obscured | Pass as far as seen | Pass after O2 fix | Untested |
| 2.5.8 Target size | Pass | Pass after F2, except O7 | Docs say 32 DIP; untested |
| 3.3.1 Error identification | Partly (O6) | Untested | Untested |
| 4.1.2 Name, role, value | Pass | Pass after F1 | Docs: labels present on every egui element; the WinUI shell is described as named |
| 4.1.3 Status messages | Partly (O5) | Partly (O5) | Untested |

## Tren

Not run. What its own documentation says: the WinUI shell uses standard controls, names its regions, keeps action targets at 32 DIP or more, and offers a keyboard path for drafting; the egui window exposes labelled elements through AccessKit. Its test matrix lists Tab and arrow-key traversal as **open**. The drawing itself is a GPU canvas with no accessibility tree. That is common for CAD, but it should be stated, and the command line and the object list are the accessible route to the drawing. To audit it, install it and run `tools/a11y/uia-audit.ps1 -Exe <Aad.WinUi.exe> -Pages <menu items>`.

## Colour contrast, calculated

Run `node tools/a11y/contrast.mjs`. All 32 pairs pass. The accents:

| App | Fill | Text on the fill | Accent as text on white |
|---|---|---|---|
| Kivo | orange `#FF6B00` | ink, 6.2:1 | `#A83F00`, 6.2:1 |
| Falcon | blue `#1F5FD1` | white, 5.8:1 | `#1A52B8`, 7.1:1 |
| Tren | black `#1B1C20` | white, 17.0:1 | `#17181B`, 17.8:1 |

Amber and orange on white are not used for small text. The orange fill against white measures 2.9:1 as a shape; the button is identified by its label (6.2:1) and its edge (3.3:1), so it passes 1.4.11.

## Re-running the audit

```powershell
cd tools\a11y
.\uia-audit.ps1 -Exe "<path to Kivo>\O-Studio.exe" -Company "<a demo company>.adms" -User owner -Password $env:AUDIT_PASSWORD -Pages Pulse,Clients,"Fee proposals" -Out studio.json
.\uia-audit.ps1 -Exe "<path to Falcon>\AQCCore.exe" -Pages Dashboard,Columns -Out metra.json
node analyse.mjs studio.json
node analyse.mjs metra.json
node contrast.mjs
```

Use a throw-away demo company, never a real one. The script types only into the system file dialog, and only while that dialog has the keyboard.

## What to do next

1. A Narrator and NVDA pass over the main tasks in Kivo and Falcon (sign in, create a client, save a record, read a table). This is the part automation cannot do, and it is the only way to confirm O5.
2. Kivo: mark the field at fault for errors that come back from the engine (O6).
3. Test at 200% text size and 400% display zoom, and in Windows high contrast.
4. Install Tren and run the same audit (see the hand-off in the OSAS repository).
