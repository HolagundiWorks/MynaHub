# UI audit and the compact density

Audited 2026-10-29 against O-Draft's polished shell (`apps/aad-winui`, its `docs/UI_TOKENS.md` and `docs/UI_STANDARDS.md`).

## What O-Draft does that O-Studio and o-ui did not

| Parameter | O-Draft | o-ui before | o-ui now |
|---|---|---|---|
| Control height (inputs, buttons) | 26 | 32 to 36 | 26 |
| Body / section / page title type | 12 / 13 / 18 | 13 / 15 / 22 | 12 / 13 / 18 |
| Caption | 11 | 12 | 11 |
| Corner radius (controls, cards) | 4 | 2 to 8, mixed | 4 |
| Button padding | 12 x 2 | 14 x 5 | 12 x 2 |
| Spacing scale | 4, 8, 12, 16, 20, 24 | 8, 12, 16, 20 | 4, 8, 12, 16, 20, 24 (`Space1` to `Space6`) |
| Document tab height | 28 | 34 (O-Studio) | 28 (`SuiteTabHeight`) |
| Menu bar / menu item | 24 / 22 | not defined | 24 / 22 |
| Status bar, panel header, property row | 22, 24, 26 | not defined | 22, 24, 26 |
| Side pane width | 240 | 260 | 240 |
| Icon size | 16 | 15 | 16 |
| Fluent minimum overrides (text box, combo, list item) | 26, 26, 22 | none | 26, 26, 22 |

Colours are unchanged: both already share the suite palette (ink `#1A2332` on `#E4EAF2` in light), and the suite keeps its own dark theme.

## What changed in o-ui

`Hcw.Ui/Themes/SuiteStyles.xaml` holds the values (the scale keys and the default styles for buttons, text boxes, password boxes, combo boxes, cards, chips and the primary button), and `SuiteScale.cs` reads them with the same fallbacks. New keys: `Space1` to `Space6`, `SuiteMenuBarHeight`, `SuiteMenuItemHeight`, `SuiteTabHeight`, `SuiteStatusBarHeight`, `SuitePanelHeaderHeight`, `SuitePropertyRowHeight`, `SuiteControlHeightCompact`, `SuiteFontCaption`, `SuiteFontIcon`, `SuiteFontDisplay`. `SuiteThemeSwitch` now takes its height from the scale.

## What changed in O-Studio

- The two tab rows use `SuiteTabHeight` (28), body type 12 and section type 13, with 4 px corners on the open tab.
- The bottom bar of other apps, the floating assistant and the Suite dashboard take their padding, gaps and type from `SuiteScale` instead of fixed numbers. Dashboard figures use `MetricValueStyle`.
- The menu bar is 12 px.
- Pages built with `Flow` already read `SuiteScale`, so they follow the new values without edits.

## Left as it was

- Hard-coded sizes remain in `Ledgers.cs` (25 places), `RecordPages.cs`, `Workflows.cs` and the start screens in `MainWindow.xaml` (32 px padding). O-Draft keeps a ceiling on these with `cargo xtask ui`; O-Studio has no such check yet.
- The glowing assistant logo keeps its own size (52), because it is a brand mark and not a control.
- Not looked at on screen: the compact sizes have been built but not reviewed in the window, in light or dark.

## Second pass: light and dark, read-only data, padding (2026-10-29)

Looked at on screen in both themes (window captures of the Firm, Practice, Accounts and Suite pages).

Bugs found and fixed:

- **Light theme was broken after switching.** Colours were looked up when a control was made and never again, so switching theme left the page, the tabs and the floating assistant in the old colours (dark text on a dark page, or the reverse), and the title bar and menu stayed light. The workspace now builds its layout again when the theme changes and shows the same page, the assistant frames take the new colours, and the main window re-applies the title bar, window frame and menu.
- **Firm data looked editable.** The fields were read-only but drawn as input boxes, and the first one had focus. Read-only they are now plain text (empty ones say "Not set"), skip the tab order, and the GST scheme and letterhead show as values. **Edit** (now at the top, under the description) turns them into input boxes with **Save profile** and **Cancel**.
- **The first version of Edit crashed the app** (it asked for a colour that does not exist). Fixed, and checked by pressing it.
- **No room around the page.** The page started directly under the second row of tabs. There is now a rule under that row and 12 px above the page.
- **The floating assistant covered the bottom of every page** and left an empty outline above the bar when closed. Pages keep 64 px clear at the bottom, and the outline goes with the panel.
- **Group names in the second row read as tabs** ("Books" looked like a disabled tab). They are now small capitals after a divider.

Not fixed, for a decision:

- Some pages show the same action twice (the **New invoice** button at the top and again at the bottom right).
- Table headers on the Invoices page mix styles (Total and Balance use a different face).
- The theme switch is a bright blue pill in dark, which does not match the orange accent. It is in o-ui.
