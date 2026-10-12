//! Phase 0 proof of concept: offline HTML/CSS -> PDF with printpdf's pure-Rust layout engine.
//! Not wired into the app yet; exercised by the tests below.

use printpdf::{
    Base64OrRaw, BuiltinFont, Color, GeneratePdfOptions, LinePoint, Op, PaintMode, PdfDocument, PdfFontHandle, PdfSaveOptions, Point,
    Polygon, PolygonRing, Pt, Rgb, TextItem, WindingOrder,
};
use std::collections::BTreeMap;

const DEJAVU: &[u8] = include_bytes!("../assets/fonts/DejaVuSans.ttf");

/// Fonts shipped with the app (DejaVu Sans, free licence in assets/fonts). Keyed by CSS family name.
pub fn bundled_fonts() -> BTreeMap<String, Base64OrRaw> {
    BTreeMap::from([("DejaVu".to_string(), Base64OrRaw::Raw(DEJAVU.to_vec()))])
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
fn qr_ops(data: &str, page_w: f32, page_h: f32) -> Vec<Op> {
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
        Op::SetFont { font: PdfFontHandle::Builtin(BuiltinFont::Helvetica), size: Pt(7.0) },
        Op::SetLineHeight { lh: Pt(8.0) },
        Op::SetFillColor { col: rgb(0.0) },
        Op::ShowText { items: vec![TextItem::Text("Scan to pay (UPI)".to_string())] },
        Op::EndTextSection,
    ]);
    ops
}

/// "Page 2 of 3", centred at the foot of the page.
fn footer_ops(page: usize, total: usize, page_w: f32) -> Vec<Op> {
    vec![
        Op::StartTextSection,
        Op::SetTextCursor { pos: Point { x: Pt(page_w / 2.0 - 24.0), y: Pt(22.0) } },
        Op::SetFont { font: PdfFontHandle::Builtin(BuiltinFont::Helvetica), size: Pt(8.0) },
        Op::SetLineHeight { lh: Pt(9.0) },
        Op::SetFillColor { col: rgb(0.3) },
        Op::ShowText { items: vec![TextItem::Text(format!("Page {page} of {total}"))] },
        Op::EndTextSection,
    ]
}

/// Like `render_html`, plus an optional QR code (the given text) drawn on the first page.
pub fn render_html_with_qr(html: &str, fonts: &BTreeMap<String, Base64OrRaw>, qr: Option<&str>) -> Result<Rendered, String> {
    let images = BTreeMap::new();
    let mut warnings = Vec::new();
    let mut doc = PdfDocument::from_html(html, &images, fonts, &GeneratePdfOptions::default(), &mut warnings)
        .map_err(|e| format!("{e:?}"))?;
    if let (Some(data), Some(page)) = (qr, doc.pages.first_mut()) {
        let (w, h) = (page.media_box.width.0, page.media_box.height.0);
        page.ops.extend(qr_ops(data, w, h));
    }
    let total = doc.pages.len();
    for (i, page) in doc.pages.iter_mut().enumerate() {
        let w = page.media_box.width.0;
        page.ops.extend(footer_ops(i + 1, total, w));
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
