//! Phase 0 proof of concept: offline HTML/CSS -> PDF with printpdf's pure-Rust layout engine.
//! Not wired into the app yet; exercised by the tests below.

use printpdf::{
    Base64OrRaw, BuiltinFont, Color, GeneratePdfOptions, LinePoint, Op, PaintMode, PdfDocument, PdfFontHandle, PdfSaveOptions, Point,
    Polygon, PolygonRing, Pt, Rgb, TextItem, WindingOrder,
};
use std::collections::BTreeMap;

const DEJAVU: &[u8] = include_bytes!("../assets/fonts/DejaVuSans.ttf");

/// The IBM Plex families (SIL Open Font License 1.1; versions and licence in assets/fonts/plex). Real font files per weight,
/// never synthesised bold or italic. Sans is the document face, Serif an optional accent, Mono for identifiers, and
/// Devanagari is the fallback for Hindi and Marathi text.
macro_rules! plex {
    ($name:literal) => {
        ($name, include_bytes!(concat!("../assets/fonts/plex/", $name, ".ttf")) as &[u8])
    };
}
const PLEX: &[(&str, &[u8])] = &[
    plex!("IBMPlexSans-Regular"), plex!("IBMPlexSans-Italic"), plex!("IBMPlexSans-Medium"), plex!("IBMPlexSans-SemiBold"), plex!("IBMPlexSans-Bold"),
    plex!("IBMPlexSerif-Regular"), plex!("IBMPlexSerif-SemiBold"),
    plex!("IBMPlexMono-Regular"), plex!("IBMPlexMono-Medium"),
    plex!("IBMPlexSansDevanagari-Regular"), plex!("IBMPlexSansDevanagari-SemiBold"),
];

/// Fonts shipped with the app. Documents use IBM Plex; DejaVu Sans stays registered as a last-resort fallback for symbols.
pub fn bundled_fonts() -> BTreeMap<String, Base64OrRaw> {
    let mut fonts: BTreeMap<String, Base64OrRaw> = PLEX.iter().map(|(n, b)| (n.to_string(), Base64OrRaw::Raw(b.to_vec()))).collect();
    fonts.insert("DejaVu".to_string(), Base64OrRaw::Raw(DEJAVU.to_vec()));
    fonts
}

pub struct Rendered {
    pub bytes: Vec<u8>,
    pub pages: usize,
    pub warnings: Vec<String>,
}

pub fn render_html(html: &str, fonts: &BTreeMap<String, Base64OrRaw>) -> Result<Rendered, String> {
    render_html_with_qr(html, fonts, None)
}

fn rgb(v: f32) -> Color {
    Color::Rgb(Rgb { r: v, g: v, b: v, icc_profile: None })
}

fn rect_op(x: f32, y: f32, w: f32, h: f32) -> Op {
    let pt = |x: f32, y: f32| LinePoint { p: Point { x: Pt(x), y: Pt(y) }, bezier: false };
    Op::DrawPolygon {
        polygon: Polygon { rings: vec![PolygonRing { points: vec![pt(x, y), pt(x + w, y), pt(x + w, y + h), pt(x, y + h)] }], mode: PaintMode::Fill, winding_order: WindingOrder::NonZero },
    }
}

/// A QR code drawn as filled rectangles at the top-right of the page (~30 mm square, with a quiet zone).
fn qr_ops(data: &str, page_w: f32, page_h: f32, font: &PdfFontHandle) -> Vec<Op> {
    use qrcode::{Color as Module, QrCode};
    let Ok(code) = QrCode::new(data.as_bytes()) else { return vec![] };
    let n = code.width();
    let modules = code.to_colors();
    let total = 96.0_f32; // ~34 mm including a 2-module quiet zone on each side
    let unit = total / (n as f32 + 4.0);
    let x0 = page_w - 36.0 - total;
    let y0 = page_h - 36.0 - total;
    let mut ops = vec![Op::SaveGraphicsState, Op::SetFillColor { col: rgb(1.0) }, rect_op(x0, y0, total, total), Op::SetFillColor { col: rgb(0.0) }];
    for row in 0..n {
        let mut col = 0;
        while col < n {
            if modules[row * n + col] == Module::Dark {
                let start = col;
                while col < n && modules[row * n + col] == Module::Dark {
                    col += 1;
                }
                let x = x0 + unit * (2.0 + start as f32);
                let y = y0 + unit * (2.0 + (n - 1 - row) as f32);
                ops.push(rect_op(x, y, unit * (col - start) as f32, unit));
            } else {
                col += 1;
            }
        }
    }
    ops.push(Op::RestoreGraphicsState);
    ops.extend([
        Op::StartTextSection,
        Op::SetTextCursor { pos: Point { x: Pt(x0 + 8.0), y: Pt(y0 - 9.0) } },
        Op::SetFont { font: font.clone(), size: Pt(8.0) },
        Op::SetLineHeight { lh: Pt(9.0) },
        Op::SetFillColor { col: rgb(0.0) },
        Op::ShowText { items: vec![TextItem::Text("Scan to pay (UPI)".to_string())] },
        Op::EndTextSection,
    ]);
    ops
}

/// The embedded IBM Plex Sans Regular face, so footers match the document and no font is left unembedded.
/// Falls back to the built-in Helvetica only if the document used no Plex text.
fn footer_font(doc: &PdfDocument) -> PdfFontHandle {
    doc.resources
        .fonts
        .map
        .iter()
        .find(|(_, f)| f.parsed_font.as_azul().font_name.as_deref().is_some_and(|n| n.replace(' ', "") == "IBMPlexSans"))
        .map(|(id, _)| PdfFontHandle::External(id.clone()))
        .unwrap_or(PdfFontHandle::Builtin(BuiltinFont::Helvetica))
}

/// "Page 2 of 3", centred at the foot of the page.
fn footer_ops(page: usize, total: usize, page_w: f32, font: &PdfFontHandle) -> Vec<Op> {
    vec![
        Op::StartTextSection,
        Op::SetTextCursor { pos: Point { x: Pt(page_w / 2.0 - 24.0), y: Pt(22.0) } },
        Op::SetFont { font: font.clone(), size: Pt(8.0) },
        Op::SetLineHeight { lh: Pt(9.0) },
        Op::SetFillColor { col: rgb(0.3) },
        Op::ShowText { items: vec![TextItem::Text(format!("Page {page} of {total}"))] },
        Op::EndTextSection,
    ]
}

/// Renders with an explicit paper size and margins (from the template's @page rule).
pub fn render_html_with_page(html: &str, fonts: &BTreeMap<String, Base64OrRaw>, setup: &crate::template_engine::PageSetup) -> Result<Rendered, String> {
    let options = GeneratePdfOptions {
        page_width: Some(setup.width_mm),
        page_height: Some(setup.height_mm),
        margin_top: Some(setup.margin_top),
        margin_right: Some(setup.margin_right),
        margin_bottom: Some(setup.margin_bottom),
        margin_left: Some(setup.margin_left),
        ..GeneratePdfOptions::default()
    };
    render_with_options(html, fonts, None, &options)
}

/// Like `render_html`, plus an optional QR code (the given text) drawn on the first page.
pub fn render_html_with_qr(html: &str, fonts: &BTreeMap<String, Base64OrRaw>, qr: Option<&str>) -> Result<Rendered, String> {
    render_with_options(html, fonts, qr, &GeneratePdfOptions::default())
}

fn render_with_options(html: &str, fonts: &BTreeMap<String, Base64OrRaw>, qr: Option<&str>, options: &GeneratePdfOptions) -> Result<Rendered, String> {
    let images = BTreeMap::new();
    let mut warnings = Vec::new();
    let mut doc = PdfDocument::from_html(html, &images, fonts, options, &mut warnings)
        .map_err(|e| format!("{e:?}"))?;
    let font = footer_font(&doc);
    if let (Some(data), Some(page)) = (qr, doc.pages.first_mut()) {
        let (w, h) = (page.media_box.width.0, page.media_box.height.0);
        page.ops.extend(qr_ops(data, w, h, &font));
    }
    let total = doc.pages.len();
    for (i, page) in doc.pages.iter_mut().enumerate() {
        let w = page.media_box.width.0;
        page.ops.extend(footer_ops(i + 1, total, w, &font));
    }
    let pages = total;
    let mut save_warnings = Vec::new();
    let bytes = doc.save(&PdfSaveOptions::default(), &mut save_warnings);
    let warnings = warnings.iter().map(|w| format!("{w:?}")).collect();
    Ok(Rendered { bytes, pages, warnings })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Names of the fonts a PDF refers to. Fonts are embedded as subsets, so their names appear in the file as plain text.
    fn font_names(pdf: &[u8]) -> Vec<String> {
        let text = String::from_utf8_lossy(pdf);
        let mut names = Vec::new();
        for part in text.split("/BaseFont") .skip(1) {
            let n: String = part.trim_start().trim_start_matches('/').chars().take_while(|c| !c.is_whitespace() && *c != '/' && *c != '>').collect();
            names.push(n);
        }
        names
    }

    fn has_glyph(bytes: &[u8], ch: char) -> bool {
        let mut warnings = Vec::new();
        printpdf::ParsedFont::from_bytes(bytes, 0, &mut warnings).is_some_and(|f| f.as_azul().lookup_glyph_index(ch as u32).is_some())
    }

    #[test]
    fn the_bundled_faces_cover_the_characters_documents_need() {
        let face = |name: &str| PLEX.iter().find(|(n, _)| *n == name).unwrap().1;
        for name in ["IBMPlexSans-Regular", "IBMPlexSans-Medium", "IBMPlexSans-SemiBold", "IBMPlexSans-Bold", "IBMPlexSerif-SemiBold", "IBMPlexMono-Regular"] {
            for ch in ['₹', 'A', 'z', '0', '9', '/', '-', '%', '&', '—', '•', '·'] {
                assert!(has_glyph(face(name), ch), "{name} has no glyph for U+{:04X}", ch as u32);
            }
        }
        // Hindi and Marathi come from the Devanagari face
        for ch in ['अ', 'क', '्', 'श'] {
            assert!(has_glyph(face("IBMPlexSansDevanagari-Regular"), ch), "Devanagari face lacks U+{:04X}", ch as u32);
        }
    }

    #[test]
    fn documents_embed_every_font_they_use_and_need_none_installed() {
        let html = format!(
            "<html><head><style>{}</style></head><body><h1>Studio X</h1><h2>Tax invoice</h2><p>No.: <span class=\"id\">INV/2026-27/0001</span></p>
             <p>Fee ₹1,25,000.00 payable. वास्तुकार सलाहकार</p><table class=\"items\"><tr><th>Item</th><th class=\"num\">Amount</th></tr>
             <tr><td>Design</td><td class=\"num\">₹50,000.00</td></tr></table></body></html>",
            crate::typography::css()
        );
        let r = render_html(&html, &bundled_fonts()).unwrap();
        let names = font_names(&r.bytes).join(" ");
        assert!(names.contains("IBMPlexSans") && names.contains("IBMPlexMono"), "{names}");
        assert!(!names.contains("DejaVu"), "documents must be set in IBM Plex, not the fallback: {names}");
        assert!(!names.contains("Helvetica"), "no unembedded built-in font may appear: {names}");
        assert!(names.contains("Devanagari"), "Hindi and Marathi text should be set in the Devanagari face: {names}");
        assert!(r.bytes.starts_with(b"%PDF"));
    }

    #[test]
    fn plex_weights_are_real_font_files_and_embedded_in_the_pdf() {
        let html = r#"<html><head><style>
            body { font-family: 'IBM Plex Sans'; font-size: 10px; font-weight: 400; }
            h1 { font-family: 'IBM Plex Serif SemiBold'; font-size: 24px; font-weight: 400; }
            .id { font-family: 'IBM Plex Mono'; font-size: 9px; }
            .m { font-family: 'IBM Plex Sans Medium'; } .s { font-family: 'IBM Plex Sans SemiBold'; } .b { font-weight: 700; }
        </style></head><body>
            <h1>Consultancy Agreement</h1>
            <p>Regular text, fee ₹1,25,000.00 payable on appointment.</p>
            <p class="m">Medium text</p><p class="s">Semibold text</p><p class="b">Bold text</p>
            <p class="id">INV/2026-27/0001 R2</p>
        </body></html>"#;
        let r = render_html(html, &bundled_fonts()).unwrap();
        let names = font_names(&r.bytes).join(" ");
        for expected in ["IBMPlexSans", "IBMPlexSerif", "IBMPlexMono"] {
            assert!(names.contains(expected), "the PDF should embed {expected}; it refers to: {names}");
        }
                // each named weight is its own embedded face (PostScript names are abbreviated by the font: Medm, SmBld)
        for face in ["Medm", "SmBld", "IBMPlexSans-Bold", "IBMPlexSerif-SemiBold"] {
            assert!(names.contains(face), "{face} should be a real embedded face, not synthesised: {names}");
        }
        assert!(!names.contains("Helvetica"), "the footer must use the embedded face, not an unembedded built-in: {names}");
    }

    fn invoice_html(rows: usize) -> String {
        let mut body = String::new();
        for i in 1..=rows {
            body.push_str(&format!(
                "<tr><td>{i}</td><td>Architectural services, stage {i}</td><td>1</td><td>₹{:.2}</td></tr>",
                i as f64 * 12500.0
            ));
        }
        format!(
            r#"<html><head><style>
              body {{ font-family: DejaVu; font-size: 11px; }}
              h1 {{ font-size: 20px; }}
              table {{ width: 100%; border-collapse: collapse; }}
              td, th {{ border: 1px solid #999999; padding: 4px; }}
            </style></head><body>
              <h1>TAX INVOICE</h1>
              <p>Bill to: Acme Developers — Villa project</p>
              <table><tr><th>#</th><th>Description</th><th>Qty</th><th>Amount</th></tr>{body}</table>
              <p>Total due: ₹1,23,456.00</p>
            </body></html>"#
        )
    }

    fn arial() -> Option<BTreeMap<String, Base64OrRaw>> {
        Some(bundled_fonts())
    }

    #[test]
    fn renders_single_page_invoice_with_rupee() {
        let Some(fonts) = arial() else { return };
        let r = render_html(&invoice_html(5), &fonts).unwrap();
        assert!(r.bytes.starts_with(b"%PDF"));
        assert_eq!(r.pages, 1);
        std::fs::write(std::env::temp_dir().join("poc_invoice.pdf"), &r.bytes).unwrap();
        println!("warnings: {:?}", r.warnings);
    }

    #[test]
    fn long_table_paginates() {
        let Some(fonts) = arial() else { return };
        let r = render_html(&invoice_html(120), &fonts).unwrap();
        std::fs::write(std::env::temp_dir().join("poc_long.pdf"), &r.bytes).unwrap();
        println!("pages: {} warnings: {:?}", r.pages, r.warnings);
        assert!(r.pages > 1);
    }
}
