//! Generates the AOS logo options from one shape definition, so every size, colour way and file stays consistent.
//! Run from the repository root:  cargo run --release --manifest-path tools/brand/Cargo.toml
//!
//! The shape is the existing mark: an "A" with a rounded apex that bleeds off the tile at the left and bottom, a bar across it, and a
//! second bar stepping down to the right. Only the colours change. Text is converted to outlines from the bundled IBM Plex Sans, so
//! no font is needed to open or render the files.
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};

const FONT: &[u8] = include_bytes!("../../../src-tauri/assets/fonts/plex/IBMPlexSans-Medium.ttf");

// ---------------------------------------------------------------- colour

#[derive(Clone, Copy)]
struct Rgb(u8, u8, u8);

impl Rgb {
    fn hex(self) -> String {
        format!("#{:02X}{:02X}{:02X}", self.0, self.1, self.2)
    }
    fn lum(self) -> f64 {
        let f = |c: u8| {
            let v = c as f64 / 255.0;
            if v <= 0.03928 { v / 12.92 } else { ((v + 0.055) / 1.055).powf(2.4) }
        };
        0.2126 * f(self.0) + 0.7152 * f(self.1) + 0.0722 * f(self.2)
    }
}

fn contrast(a: Rgb, b: Rgb) -> f64 {
    let (x, y) = (a.lum(), b.lum());
    (x.max(y) + 0.05) / (x.min(y) + 0.05)
}

/// The four roles in the mark. `field` may be absent (the mark sits straight on the surface).
#[derive(Clone, Copy)]
struct Palette {
    field: Option<Rgb>,
    letter: Rgb,
    bar_on_letter: Rgb,
    bar_on_field: Rgb,
    /// The surface the mark is meant for, used for contrast checks and the preview.
    surface: Rgb,
    text: Rgb,
}

struct Option_ {
    key: &'static str,
    name: &'static str,
    idea: &'static str,
    standard: Palette,
    reversed: Palette,
    mono: Rgb,
}

fn options() -> Vec<Option_> {
    let white = Rgb(255, 255, 255);
    // Option A keeps the yellow but gives the letter an ink body, so the shape no longer depends on white-on-yellow.
    let ink = Rgb(0x17, 0x18, 0x1B);
    let amber = Rgb(0xFF, 0xC2, 0x0E);
    // Option B: deep teal and warm paper with terracotta, an architectural palette that works on light and dark.
    let teal = Rgb(0x0F, 0x4C, 0x5C);
    let paper = Rgb(0xF6, 0xF1, 0xE7);
    let terracotta = Rgb(0xC2, 0x41, 0x0C);
    let peach = Rgb(0xF6, 0xA2, 0x7A);
    let night = Rgb(0x1B, 0x1D, 0x21);
    vec![
        Option_ {
            key: "option-a",
            name: "Option A: Amber and Ink",
            idea: "Keeps the yellow identity. The letter is now ink on amber, so it holds in black and white and at 16 pixels. On dark surfaces the mark drops its tile: an amber letter with a deep-amber bar. On white the amber tile edge is soft (1.6:1), so the ink letter carries the shape; use the one-colour mark where a hard edge is needed.",
            standard: Palette { field: Some(amber), letter: ink, bar_on_letter: white, bar_on_field: ink, surface: white, text: ink },
            reversed: Palette { field: None, letter: amber, bar_on_letter: Rgb(0x6B, 0x4F, 0x00), bar_on_field: amber, surface: night, text: Rgb(0xF4, 0xF1, 0xEA) },
            mono: ink,
        },
        Option_ {
            key: "option-b",
            name: "Option B: Teal and Terracotta",
            idea: "A calmer, more architectural palette: a deep teal tile with a warm paper letter and terracotta bars. On dark surfaces the tile turns to warm paper so it stays a bright, readable shape.",
            standard: Palette { field: Some(teal), letter: paper, bar_on_letter: terracotta, bar_on_field: peach, surface: white, text: teal },
            reversed: Palette { field: Some(paper), letter: teal, bar_on_letter: peach, bar_on_field: terracotta, surface: night, text: paper },
            mono: teal,
        },
    ]
}

// ---------------------------------------------------------------- shape (1024 x 1024 tile)

/// The letter: an inverted V with a rounded apex and a notch, cut off by the tile at the left and bottom.
const LETTER: &str = "M -171 1024 L 245 338 Q 340 220 438 338 L 849 1024 L 606 1024 L 333 570 L 66 1024 Z";
/// The bar across the letter, and the step that continues down to the right.
const BAR_ONE: &str = "M 272 675 L 568 675 L 666 842 L 172 842 Z";
const BAR_TWO: &str = "M 668 842 L 913 842 L 1018 1024 L 772 1024 Z";

fn tile(id: &str, p: &Palette, with_field: bool) -> String {
    let mut s = String::new();
    let _ = write!(s, "<clipPath id=\"{id}\"><rect width=\"1024\" height=\"1024\"/></clipPath><g clip-path=\"url(#{id})\">");
    if with_field {
        if let Some(f) = p.field {
            let _ = write!(s, "<rect width=\"1024\" height=\"1024\" fill=\"{}\"/>", f.hex());
        }
    }
    let _ = write!(s, "<path d=\"{LETTER}\" fill=\"{}\"/>", p.letter.hex());
    let _ = write!(s, "<path d=\"{BAR_ONE}\" fill=\"{}\"/>", p.bar_on_letter.hex());
    let _ = write!(s, "<path d=\"{BAR_TWO}\" fill=\"{}\"/></g>", p.bar_on_field.hex());
    s
}

/// One colour on a transparent ground: the letter in the colour, the bars cut out of it, the second bar solid.
fn mono_tile(id: &str, c: Rgb) -> String {
    let mut s = String::new();
    let _ = write!(
        s,
        "<clipPath id=\"{id}c\"><rect width=\"1024\" height=\"1024\"/></clipPath><mask id=\"{id}m\"><rect x=\"-300\" width=\"1700\" height=\"1024\" fill=\"#fff\"/><path d=\"{BAR_ONE}\" fill=\"#000\"/></mask>\
         <g clip-path=\"url(#{id}c)\" fill=\"{}\"><path d=\"{LETTER}\" mask=\"url(#{id}m)\"/><path d=\"{BAR_TWO}\"/></g>",
        c.hex()
    );
    s
}

// ---------------------------------------------------------------- text as outlines

struct Pen {
    d: String,
    scale: f64,
    x: f64,
    y: f64,
}

impl ttf_parser::OutlineBuilder for Pen {
    fn move_to(&mut self, x: f32, y: f32) {
        let _ = write!(self.d, "M{:.1} {:.1}", self.x + x as f64 * self.scale, self.y - y as f64 * self.scale);
    }
    fn line_to(&mut self, x: f32, y: f32) {
        let _ = write!(self.d, "L{:.1} {:.1}", self.x + x as f64 * self.scale, self.y - y as f64 * self.scale);
    }
    fn quad_to(&mut self, x1: f32, y1: f32, x: f32, y: f32) {
        let (sx, sy) = (self.x, self.y);
        let sc = self.scale;
        let _ = write!(self.d, "Q{:.1} {:.1} {:.1} {:.1}", sx + x1 as f64 * sc, sy - y1 as f64 * sc, sx + x as f64 * sc, sy - y as f64 * sc);
    }
    fn curve_to(&mut self, x1: f32, y1: f32, x2: f32, y2: f32, x: f32, y: f32) {
        let (sx, sy) = (self.x, self.y);
        let sc = self.scale;
        let _ = write!(
            self.d,
            "C{:.1} {:.1} {:.1} {:.1} {:.1} {:.1}",
            sx + x1 as f64 * sc, sy - y1 as f64 * sc, sx + x2 as f64 * sc, sy - y2 as f64 * sc, sx + x as f64 * sc, sy - y as f64 * sc
        );
    }
    fn close(&mut self) {
        self.d.push('Z');
    }
}

const TRACKING: f64 = 0.06; // letter-spacing in em

/// Width of the text at 1 em, including tracking.
fn width_em(face: &ttf_parser::Face, text: &str) -> f64 {
    let upem = face.units_per_em() as f64;
    text.chars().filter_map(|c| face.glyph_index(c)).map(|g| face.glyph_hor_advance(g).unwrap_or(0) as f64 / upem + TRACKING).sum::<f64>() - TRACKING
}

/// Outline path for `text` with its left edge at `x` and baseline at `y`, `size` px tall per em.
fn text_path(face: &ttf_parser::Face, text: &str, x: f64, y: f64, size: f64, fill: Rgb) -> String {
    let upem = face.units_per_em() as f64;
    let scale = size / upem;
    let mut pen = Pen { d: String::new(), scale, x, y };
    for c in text.chars() {
        let Some(g) = face.glyph_index(c) else { continue };
        pen.x = x + (pen.x - x).max(0.0);
        face.outline_glyph(g, &mut pen);
        pen.x += face.glyph_hor_advance(g).unwrap_or(0) as f64 * scale + TRACKING * size;
    }
    format!("<path d=\"{}\" fill=\"{}\"/>", pen.d, fill.hex())
}

/// A line of text scaled so that its width is exactly `width`. Returns the path and the cap height used.
fn justified(face: &ttf_parser::Face, text: &str, x: f64, baseline: f64, width: f64, fill: Rgb) -> (String, f64) {
    let size = width / width_em(face, text);
    (text_path(face, text, x, baseline, size, fill), cap_em(face) * size)
}

fn cap_em(face: &ttf_parser::Face) -> f64 {
    face.capital_height().map(|h| h as f64).unwrap_or(0.7 * face.units_per_em() as f64) / face.units_per_em() as f64
}

/// The tile with the three-line name beside it, every line the same width as in the original lockup.
fn lockup(face: &ttf_parser::Face, id: &str, p: &Palette, lines: [&str; 3]) -> (String, f64, f64) {
    let gap_em = 0.07; // space between lines, as a fraction of the line width
    let caps: f64 = lines.iter().map(|l| cap_em(face) / width_em(face, l)).sum();
    let width = 1024.0 / (caps + 2.0 * gap_em);
    let x0 = 1024.0 + 150.0;
    let mut s = tile(id, p, true);
    let mut y = 0.0;
    for l in lines {
        let size = width / width_em(face, l);
        let cap = cap_em(face) * size;
        let (path, _) = justified(face, l, x0, y + cap, width, p.text);
        s.push_str(&path);
        y += cap + gap_em * width;
    }
    // a margin so letters that overshoot the cap line (C, S, U) are never clipped by the edge of the file
    const PAD: f64 = 30.0;
    (format!("<g transform=\"translate({PAD} {PAD})\">{s}</g>"), x0 + width + 2.0 * PAD, 1024.0 + 2.0 * PAD)
}

// ---------------------------------------------------------------- output

fn svg(w: f64, h: f64, body: &str) -> String {
    format!("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w:.0}\" height=\"{h:.0}\" viewBox=\"0 0 {w:.0} {h:.0}\">{body}</svg>")
}

fn render(svg_text: &str, width: u32, path: &Path) {
    let tree = resvg::usvg::Tree::from_str(svg_text, &resvg::usvg::Options::default()).expect("valid SVG");
    let size = tree.size();
    let height = (width as f32 * size.height() / size.width()).round().max(1.0) as u32;
    let mut pixmap = resvg::tiny_skia::Pixmap::new(width, height).expect("pixmap");
    let scale = width as f32 / size.width();
    resvg::render(&tree, resvg::tiny_skia::Transform::from_scale(scale, scale), &mut pixmap.as_mut());
    pixmap.save_png(path).expect("write png");
}

fn png_bytes(svg_text: &str, side: u32) -> Vec<u8> {
    let tree = resvg::usvg::Tree::from_str(svg_text, &resvg::usvg::Options::default()).expect("valid SVG");
    let mut pixmap = resvg::tiny_skia::Pixmap::new(side, side).expect("pixmap");
    let scale = side as f32 / tree.size().width();
    resvg::render(&tree, resvg::tiny_skia::Transform::from_scale(scale, scale), &mut pixmap.as_mut());
    pixmap.encode_png().expect("encode png")
}

/// A Windows icon holding PNG images at the standard sizes.
fn ico(svg_text: &str) -> Vec<u8> {
    let sizes = [16u32, 24, 32, 48, 64, 128, 256];
    let images: Vec<Vec<u8>> = sizes.iter().map(|s| png_bytes(svg_text, *s)).collect();
    let mut out = Vec::new();
    out.extend([0, 0, 1, 0]);
    out.extend((sizes.len() as u16).to_le_bytes());
    let mut offset = 6 + 16 * sizes.len() as u32;
    for (s, img) in sizes.iter().zip(&images) {
        out.push(if *s >= 256 { 0 } else { *s as u8 });
        out.push(if *s >= 256 { 0 } else { *s as u8 });
        out.extend([0, 0, 1, 0, 32, 0]);
        out.extend((img.len() as u32).to_le_bytes());
        out.extend(offset.to_le_bytes());
        offset += img.len() as u32;
    }
    for img in images {
        out.extend(img);
    }
    out
}

fn checks(o: &Option_) -> Vec<(String, f64)> {
    let mut v = Vec::new();
    for (label, p) in [("standard", &o.standard), ("reversed", &o.reversed)] {
        let ground = p.field.unwrap_or(p.surface);
        v.push((format!("{label}: letter on its ground"), contrast(p.letter, ground)));
        v.push((format!("{label}: bar on the letter"), contrast(p.bar_on_letter, p.letter)));
        v.push((format!("{label}: bar on the ground"), contrast(p.bar_on_field, ground)));
        v.push((format!("{label}: name on its surface"), contrast(p.text, p.surface)));
        if let Some(f) = p.field {
            v.push((format!("{label}: tile edge against the surface"), contrast(f, p.surface)));
        }
    }
    v
}

fn main() {
    let out = PathBuf::from("brand");
    fs::create_dir_all(&out).unwrap();
    let face = ttf_parser::Face::parse(FONT, 0).expect("font");
    let name = ["ARCHITECTURE", "OFFICE", "SUIT"];
    let mut report = String::from("# AOS logo options\n\nThe shape is unchanged from the original mark. Only the colours differ. **Option A (Amber and Ink) is the chosen logo** and is what the app, the window icon and the installer icons use (native/Adms.Desktop/Assets and src-tauri/icons were made from brand/option-a). Option B is kept for reference.\n\n");
    let mut preview = String::new();
    let (mut y, card_w) = (0.0, 3000.0);

    for o in options() {
        let dir = out.join(o.key);
        fs::create_dir_all(&dir).unwrap();
        // files
        for (tag, p) in [("standard", o.standard), ("reversed", o.reversed)] {
            let t = svg(1024.0, 1024.0, &tile("c", &p, true));
            fs::write(dir.join(format!("mark-{tag}.svg")), &t).unwrap();
            render(&t, 1024, &dir.join(format!("mark-{tag}.png")));
            let (l, w, h) = lockup(&face, "c", &p, name);
            let ls = svg(w, h, &l);
            fs::write(dir.join(format!("lockup-{tag}.svg")), &ls).unwrap();
            render(&ls, 1600, &dir.join(format!("lockup-{tag}.png")));
        }
        let mono = svg(1024.0, 1024.0, &mono_tile("m", o.mono));
        fs::write(dir.join("mark-one-colour.svg"), &mono).unwrap();
        render(&mono, 1024, &dir.join("mark-one-colour.png"));
        let std_tile = svg(1024.0, 1024.0, &tile("c", &o.standard, true));
        fs::write(dir.join("app.ico"), ico(&std_tile)).unwrap();

        // report
        let _ = writeln!(report, "## {}\n\n{}\n", o.name, o.idea);
        let _ = writeln!(report, "| Colours | Standard (light surfaces, app icon) | Reversed (dark surfaces) |\n|---|---|---|");
        let cell = |p: &Palette| {
            format!(
                "tile {}, letter {}, bar on letter {}, bar on tile {}",
                p.field.map(|f| f.hex()).unwrap_or_else(|| "none".into()), p.letter.hex(), p.bar_on_letter.hex(), p.bar_on_field.hex()
            )
        };
        let _ = writeln!(report, "| Roles | {} | {} |\n", cell(&o.standard), cell(&o.reversed));
        let _ = writeln!(report, "Contrast ratios (WCAG; 3:1 is the minimum for graphics, 4.5:1 for text):\n");
        for (label, ratio) in checks(&o) {
            let _ = writeln!(report, "- {label}: {ratio:.1}:1{}", if ratio < 3.0 { "  (below 3:1)" } else { "" });
        }
        let _ = writeln!(report);

        // preview card
        let card_h = 1700.0;
        let _ = write!(preview, "<g transform=\"translate(0 {y})\">");
        let head = text_path(&face, &o.name.to_uppercase(), 60.0, 110.0, 62.0, Rgb(0x16, 0x16, 0x16));
        preview.push_str(&head);
        let sub = text_path(&face, "STANDARD ON WHITE", 60.0, 215.0, 30.0, Rgb(0x66, 0x66, 0x66));
        preview.push_str(&sub);
        let sub2 = text_path(&face, "REVERSED ON DARK", 1560.0, 215.0, 30.0, Rgb(0x66, 0x66, 0x66));
        preview.push_str(&sub2);
        // surfaces
        let _ = write!(preview, "<rect x=\"40\" y=\"240\" width=\"1460\" height=\"1380\" fill=\"#FFFFFF\" stroke=\"#D0D0D0\"/><rect x=\"1540\" y=\"240\" width=\"1420\" height=\"1380\" fill=\"{}\"/>", o.reversed.surface.hex());
        for (i, (p, left)) in [(o.standard, 40.0), (o.reversed, 1540.0)].into_iter().enumerate() {
            let id = |n: &str| format!("{n}{i}{}", o.key.len());
            // large tile
            let _ = write!(preview, "<g transform=\"translate({} 290) scale(0.5)\">{}</g>", left + 60.0, tile(&id("t"), &p, true));
            // lockup
            let (l, w, h) = lockup(&face, &id("l"), &p, name);
            let s = 640.0 / w;
            let _ = write!(preview, "<g transform=\"translate({} 380) scale({s})\">{l}</g>", left + 620.0);
            let _ = h;
            // small sizes
            let mut x = left + 60.0;
            for side in [128.0, 64.0, 32.0, 16.0] {
                let sc = side / 1024.0;
                let _ = write!(preview, "<g transform=\"translate({x} 880) scale({sc})\">{}</g>", tile(&id(&format!("s{}", side as i32)), &p, true));
                x += side + 36.0;
            }
            // greyscale (luminance only), to prove the shape does not rely on hue
            let _ = write!(
                preview,
                "<filter id=\"{g}\"><feColorMatrix type=\"saturate\" values=\"0\"/></filter><g filter=\"url(#{g})\" transform=\"translate({} 1080) scale(0.3)\">{}</g>",
                left + 60.0, tile(&id("g"), &p, true), g = id("grey")
            );
            let grey_label = text_path(&face, "GREYSCALE", left + 400.0, 1260.0, 26.0, if i == 0 { Rgb(0x66, 0x66, 0x66) } else { Rgb(0xAA, 0xAA, 0xAA) });
            preview.push_str(&grey_label);
        }
        preview.push_str("</g>");
        y += card_h;
    }
    let sheet = svg(card_w, y, &format!("<rect width=\"{card_w}\" height=\"{y}\" fill=\"#EDEDED\"/>{preview}"));
    fs::write(out.join("preview.svg"), &sheet).unwrap();
    render(&sheet, 2200, &out.join("preview.png"));
    fs::write(out.join("README.md"), report).unwrap();
    println!("written to {}", out.display());
}
