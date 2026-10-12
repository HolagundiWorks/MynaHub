# Bundled document fonts: IBM Plex

| Family | Files | Used for |
|---|---|---|
| IBM Plex Sans | Regular, Italic, Medium, SemiBold, Bold | The document face: body, labels, headings, tables, footers |
| IBM Plex Serif | Regular, SemiBold | Optional formal title (agreements) |
| IBM Plex Mono | Regular, Medium | Document numbers, references, identifiers |
| IBM Plex Sans Devanagari | Regular, SemiBold | Fallback for Hindi and Marathi text |

- **Source:** the `@ibm/plex` npm package, version **6.4.1** (https://github.com/IBM/plex), the `complete/woff` files.
- **Licence:** SIL Open Font License 1.1, copyright IBM Corp. with Reserved Font Name "Plex". Full text in `IBM-Plex-LICENSE-OFL.txt`. The fonts are embedded (as subsets) in generated PDFs, which the licence permits.
- **Conversion:** the PDF engine reads TrueType, so each WOFF 1.0 file was unpacked to plain `.ttf` by decompressing its tables unchanged (no outlines, hinting or metrics were altered). Unmodified fonts keep the Reserved Font Name.
- **Weights are real font files**, selected by family name (for example `IBM Plex Sans SemiBold`). Bold and italic are never synthesised. Named styles live in `src/typography.rs`; templates cannot choose fonts.
- DejaVu Sans (`../DejaVuSans.ttf`, licence beside it) stays registered only as a last-resort symbol fallback.
- To update: replace the package version, regenerate the `.ttf` files, update this file, and re-run the font tests in `src/pdf.rs`.
