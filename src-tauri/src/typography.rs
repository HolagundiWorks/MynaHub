//! The document type system. Every generated document (letters, agreements, records, invoices, receipts, credit notes)
//! takes its type from here, so templates choose from named styles and never set their own fonts, sizes or weights.
//!
//! Families (IBM Plex, SIL Open Font License 1.1; files and licence in assets/fonts/plex), bundled and embedded in every PDF:
//! - IBM Plex Sans: the document face. Regular for body, Medium and SemiBold for labels and headings (each its own real
//!   font file, selected by family name; weights are never synthesised).
//! - IBM Plex Serif SemiBold: an optional accent for a formal title.
//! - IBM Plex Mono: document numbers, references and other identifiers.
//! - IBM Plex Sans Devanagari: fallback for Hindi and Marathi text.
//!
//! Sizes are in points for A4 print and are starting defaults to be tuned against sample PDFs.

/// Named type tokens: (name, family, size in pt). One place to read and change the scale.
pub const SCALE: &[(&str, &str, f32)] = &[
    ("title", "IBM Plex Sans SemiBold", 22.0),
    ("title-serif", "IBM Plex Serif SemiBold", 22.0),
    ("firm", "IBM Plex Sans SemiBold", 16.0),
    ("section", "IBM Plex Sans SemiBold", 12.0),
    ("subsection", "IBM Plex Sans Medium", 10.5),
    ("body", "IBM Plex Sans", 10.0),
    ("table", "IBM Plex Sans", 9.0),
    ("table-head", "IBM Plex Sans Medium", 9.0),
    ("meta", "IBM Plex Sans", 8.5),
    ("identifier", "IBM Plex Mono", 9.0),
];

fn token(name: &str) -> (&'static str, f32) {
    SCALE.iter().find(|(n, _, _)| *n == name).map(|(_, f, s)| (*f, *s)).expect("known type token")
}

fn style(name: &str) -> String {
    let (family, size) = token(name);
    // font-weight is reset to normal: the weight is in the family (a real font file), so no bold is faked on top of it.
    format!("font-family: '{family}', 'IBM Plex Sans Devanagari', DejaVu; font-size: {size}pt; font-weight: 400;")
}

/// The shared stylesheet for every generated document.
pub fn css() -> String {
    format!(
        "body {{ {body} color: #161616; line-height: 1.45; }}
        h1 {{ {firm} margin-bottom: 2pt; }}
        h2 {{ {title} margin-top: 10pt; margin-bottom: 4pt; }}
        h2.formal {{ {title_serif} }}
        h3 {{ {section} margin-top: 8pt; margin-bottom: 2pt; }}
        h4 {{ {sub} margin-bottom: 2pt; }}
        .items {{ width: 100%; border-collapse: collapse; margin-top: 8pt; {table} line-height: 1.3; }}
        .items th, .items td {{ border: 1pt solid #8d8d8d; padding: 4pt; }}
        .items th {{ {head} background-color: #e0e0e0; }}
        .meta {{ {meta} color: #393939; }}
        .id {{ {id} }}
        .num {{ text-align: right; }}",
        body = style("body"), firm = style("firm"), title = style("title"), title_serif = style("title-serif"), section = style("section"),
        sub = style("subsection"), table = style("table"), head = style("table-head"), meta = style("meta"), id = style("identifier"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_scale_stays_inside_the_print_ranges_of_the_specification() {
        let size = |n: &str| token(n).1;
        assert!((22.0..=30.0).contains(&size("title")) && (22.0..=30.0).contains(&size("title-serif")));
        assert!((12.0..=15.0).contains(&size("section")));
        assert!((10.0..=11.0).contains(&size("subsection")));
        assert!((9.5..=10.5).contains(&size("body")));
        assert!((8.5..=9.5).contains(&size("table")) && (8.5..=9.5).contains(&size("table-head")));
        assert!((8.0..=9.0).contains(&size("meta")));
        assert!((8.5..=9.5).contains(&size("identifier")));
        for (name, _, s) in SCALE {
            assert!(*s >= 8.0, "{name} is below the 8 pt floor");
        }
    }

    #[test]
    fn only_approved_families_are_used() {
        for (_, family, _) in SCALE {
            assert!(["IBM Plex Sans", "IBM Plex Sans Medium", "IBM Plex Sans SemiBold", "IBM Plex Serif SemiBold", "IBM Plex Mono"].contains(family), "{family}");
        }
        let css = css();
        assert!(!css.contains("Helvetica") && !css.contains("Arial"));
    }
}
