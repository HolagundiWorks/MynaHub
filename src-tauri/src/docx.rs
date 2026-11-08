//! Office Open XML (.docx) templates. A template is a Word document. The PDF renderer still
//! lays out the letter from the document text, so a preview is the same file an issued document uses.
use base64::Engine;
use std::collections::BTreeMap;

const NS_W: &str = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
const NS_R: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

pub fn present(html: &str, css: &str) -> Result<(String, String), String> {
    if is_docx(html, css) {
        unzip(&decode(html)?)?;
        return Ok((html.trim().to_string(), "docx".into()));
    }
    store(html, css)
}

/// Stores a template as a Word document. HTML from an older template is packaged, not kept as the file.
pub fn store(html: &str, css: &str) -> Result<(String, String), String> {
    if is_docx(html, css) {
        unzip(&decode(html)?)?;
        return Ok((html.trim().to_string(), "docx".into()));
    }
    let page = if css.trim().is_empty() { default_page().to_string() } else { css.to_string() };
    let (header, body, footer) = split_bands(html);
    let (width, height) = page_twips(&page);
    let mut files = BTreeMap::new();
    files.insert("[Content_Types].xml".into(), content_types().as_bytes().to_vec());
    files.insert("_rels/.rels".into(), package_rels().as_bytes().to_vec());
    files.insert("word/_rels/document.xml.rels".into(), document_rels().as_bytes().to_vec());
    files.insert("word/styles.xml".into(), styles().as_bytes().to_vec());
    files.insert("word/document.xml".into(), document_xml(&blocks_xml(&read_blocks(&body)), width, height).into_bytes());
    files.insert("word/header1.xml".into(), band_xml("hdr", &blocks_xml(&read_blocks(&header))).into_bytes());
    files.insert("word/footer1.xml".into(), band_xml("ftr", &blocks_xml(&read_blocks(&footer))).into_bytes());
    files.insert("word/adms-source.html".into(), html.as_bytes().to_vec());
    files.insert("word/adms-page.txt".into(), page.into_bytes());
    Ok((base64::engine::general_purpose::STANDARD.encode(zip_store(&files)), "docx".into()))
}

/// The letter text and print styles inside a Word template, ready for the PDF renderer.
pub fn prepare(html: &str, css: &str) -> Result<(String, String), String> {
    if !is_docx(html, css) {
        return Ok((html.to_string(), css.to_string()));
    }
    let files = unzip(&decode(html)?)?;
    if let Some(source) = files.get("word/adms-source.html") {
        let page = files.get("word/adms-page.txt").map(|b| String::from_utf8_lossy(b).into_owned()).unwrap_or_else(|| default_page().to_string());
        return Ok((String::from_utf8_lossy(source).into_owned(), page));
    }
    Ok(ooxml_to_html(&files))
}

#[cfg(test)]
pub fn source_html(payload: &str) -> Result<String, String> {
    let files = unzip(&decode(payload)?)?;
    files.get("word/adms-source.html").map(|b| String::from_utf8_lossy(b).into_owned()).ok_or_else(|| "This Word document has no letter text.".into())
}

fn is_docx(html: &str, css: &str) -> bool {
    css.trim() == "docx" || html.trim_start().starts_with("UEsDB")
}

fn decode(payload: &str) -> Result<Vec<u8>, String> {
    base64::engine::general_purpose::STANDARD.decode(payload.trim()).map_err(|_| "The Word document could not be read.".to_string())
}

fn default_page() -> &'static str {
    "@page { size: A4; margin: 18mm 16mm 20mm 16mm; }"
}

fn page_twips(css: &str) -> (u32, u32) {
    let lower = css.to_lowercase();
    let landscape = lower.contains("landscape");
    let (w, h) = if lower.contains("a3") {
        (16838, 23811)
    } else if lower.contains("a5") {
        (8391, 11906)
    } else {
        (11906, 16838)
    };
    if landscape { (h, w) } else { (w, h) }
}

fn split_bands(html: &str) -> (String, String, String) {
    let (header, rest) = take_band(html, "header");
    let (footer, body) = take_band(&rest, "footer");
    (header, body, footer)
}

fn take_band(html: &str, tag: &str) -> (String, String) {
    let needle = format!("<{tag}");
    let Some(start) = html.find(&needle) else { return (String::new(), html.to_string()) };
    let (_, outer, next) = read_element(html, start);
    if outer.is_empty() {
        return (String::new(), html.to_string());
    }
    (inner(&outer).to_string(), format!("{}{}", &html[..start], &html[next..]))
}

enum Block {
    Para { style: String, text: String, bold: bool },
    Line,
    Table(Vec<Vec<String>>),
}

fn read_blocks(html: &str) -> Vec<Block> {
    let mut blocks = Vec::new();
    let mut i = 0;
    while i < html.len() {
        while i < html.len() && html[i..].chars().next().is_some_and(|c| c.is_whitespace()) {
            i += html[i..].chars().next().unwrap().len_utf8();
        }
        if i >= html.len() {
            break;
        }
        if html[i..].starts_with("{{") {
            let end = html[i..].find("}}").map(|n| i + n + 2).unwrap_or(html.len());
            blocks.push(Block::Para { style: "Normal".into(), text: html[i..end].to_string(), bold: false });
            i = end;
            continue;
        }
        if html[i..].starts_with("<!--") {
            i = html[i..].find("-->").map(|n| i + n + 3).unwrap_or(html.len());
            continue;
        }
        if !html[i..].starts_with('<') {
            i += 1;
            continue;
        }
        let (name, outer, next) = read_element(html, i);
        i = next;
        if name.is_empty() {
            continue;
        }
        if name == "header" || name == "footer" {
            blocks.extend(read_blocks(inner(&outer)));
            continue;
        }
        if name == "table" {
            blocks.push(Block::Table(read_table(&outer)));
            continue;
        }
        if name == "hr" || outer.contains("class=\"rule\"") {
            blocks.push(Block::Line);
            continue;
        }
        if name == "ul" || name == "ol" {
            blocks.extend(read_blocks(inner(&outer)));
            continue;
        }
        let style = match name.as_str() {
            "h1" => "Heading1",
            "h2" => "Heading2",
            "h3" | "h4" | "h5" | "h6" => "Heading3",
            _ => "Normal",
        };
        let bold = name.starts_with('h') || outer.contains("firm-name") || outer.contains("minimal-name");
        blocks.push(Block::Para { style: style.into(), text: plain(inner(&outer)), bold });
    }
    blocks
}

fn read_table(outer: &str) -> Vec<Vec<String>> {
    let mut rows = Vec::new();
    let mut i = outer.find('>').map(|n| n + 1).unwrap_or(0);
    while i < outer.len() {
        while i < outer.len() && outer[i..].chars().next().is_some_and(|c| c.is_whitespace()) {
            i += outer[i..].chars().next().unwrap().len_utf8();
        }
        if i >= outer.len() {
            break;
        }
        if outer[i..].starts_with("{{") {
            let end = outer[i..].find("}}").map(|n| i + n + 2).unwrap_or(outer.len());
            rows.push(vec![outer[i..end].to_string()]);
            i = end;
            continue;
        }
        let start = outer[i..].find('<').map(|n| i + n).unwrap_or(outer.len());
        if start >= outer.len() {
            break;
        }
        let (name, element, next) = read_element(outer, start);
        i = next;
        if name == "tr" {
            let mut cells = Vec::new();
            let mut c = 0;
            let row = inner(&element);
            while c < row.len() {
                let at = row[c..].find('<').map(|n| c + n).unwrap_or(row.len());
                if at >= row.len() {
                    break;
                }
                let (cell_name, cell, cell_next) = read_element(row, at);
                c = cell_next;
                if cell_name == "td" || cell_name == "th" {
                    cells.push(plain(inner(&cell)));
                }
            }
            if !cells.is_empty() {
                rows.push(cells);
            }
        }
    }
    rows
}

fn inner(outer: &str) -> &str {
    let start = outer.find('>').map(|i| i + 1).unwrap_or(0);
    let end = outer.rfind("</").unwrap_or(outer.len());
    if end > start { &outer[start..end] } else { "" }
}

fn plain(html: &str) -> String {
    let mut out = String::new();
    let mut i = 0;
    while i < html.len() {
        if html[i..].starts_with('<') {
            let gt = html[i..].find('>').map(|n| i + n).unwrap_or(html.len() - 1);
            let tag = html[i + 1..gt].trim().trim_start_matches('/').trim().to_lowercase();
            if tag.starts_with("br") {
                out.push('\n');
            }
            i = gt + 1;
            continue;
        }
        let next = html[i..].find('<').map(|n| i + n).unwrap_or(html.len());
        out.push_str(&decode_entities(&html[i..next]));
        i = next;
    }
    out.trim().to_string()
}

fn decode_entities(text: &str) -> String {
    text.replace("&nbsp;", " ").replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&#39;", "'")
}

fn blocks_xml(blocks: &[Block]) -> String {
    let mut xml = String::new();
    for block in blocks {
        match block {
            Block::Line => xml.push_str("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"1A1A1A\"/></w:pBdr></w:pPr></w:p>"),
            Block::Para { style, text, bold } => xml.push_str(&paragraph_xml(style, text, *bold)),
            Block::Table(rows) => xml.push_str(&table_xml(rows)),
        }
    }
    xml
}

fn paragraph_xml(style: &str, text: &str, bold: bool) -> String {
    let mut runs = String::new();
    for (n, line) in text.split('\n').enumerate() {
        if n > 0 {
            runs.push_str("<w:r><w:br/></w:r>");
        }
        runs.push_str("<w:r><w:rPr>");
        runs.push_str("<w:rFonts w:ascii=\"IBM Plex Sans\" w:hAnsi=\"IBM Plex Sans\"/>");
        if bold {
            runs.push_str("<w:b/>");
        }
        let size = match style {
            "Heading1" => "44",
            "Heading2" => "32",
            "Heading3" => "26",
            _ => "22",
        };
        runs.push_str(&format!("<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{}</w:t></w:r>", xml_escape(line)));
    }
    format!("<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{runs}</w:p>")
}

fn table_xml(rows: &[Vec<String>]) -> String {
    let mut xml = String::from("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"B4B4B4\"/></w:tblBorders></w:tblPr>");
    for (r, row) in rows.iter().enumerate() {
        xml.push_str("<w:tr>");
        for cell in row {
            let tag_bold = r == 0;
            xml.push_str("<w:tc><w:p>");
            xml.push_str(&format!("<w:r><w:rPr>{}</w:rPr><w:t xml:space=\"preserve\">{}</w:t></w:r>", if tag_bold { "<w:b/>" } else { "" }, xml_escape(cell)));
            xml.push_str("</w:p></w:tc>");
        }
        xml.push_str("</w:tr>");
    }
    xml.push_str("</w:tbl>");
    xml
}

fn document_xml(body: &str, width: u32, height: u32) -> String {
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
<w:document xmlns:w=\"{NS_W}\" xmlns:r=\"{NS_R}\"><w:body>{body}\
<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/><w:footerReference w:type=\"default\" r:id=\"rId3\"/>\
<w:pgSz w:w=\"{width}\" w:h=\"{height}\"/><w:pgMar w:top=\"1021\" w:right=\"907\" w:bottom=\"1134\" w:left=\"907\" w:header=\"340\" w:footer=\"340\" w:gutter=\"0\"/></w:sectPr>\
</w:body></w:document>"
    )
}

fn band_xml(kind: &str, body: &str) -> String {
    format!("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:{kind} xmlns:w=\"{NS_W}\">{body}</w:{kind}>")
}

fn xml_escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

fn content_types() -> &'static str {
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\
<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\
<Default Extension=\"xml\" ContentType=\"application/xml\"/>\
<Default Extension=\"html\" ContentType=\"text/html\"/>\
<Default Extension=\"txt\" ContentType=\"text/plain\"/>\
<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>\
<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>\
<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>\
<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>\
</Types>"
}

fn package_rels() -> &'static str {
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>\
</Relationships>"
}

fn document_rels() -> &'static str {
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\
<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\
<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>\
<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>\
</Relationships>"
}

fn styles() -> &'static str {
    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\
<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">\
<w:style w:type=\"paragraph\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>\
<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/></w:style>\
<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/></w:style>\
<w:style w:type=\"paragraph\" w:styleId=\"Heading3\"><w:name w:val=\"heading 3\"/></w:style>\
</w:styles>"
}

fn ooxml_to_html(files: &BTreeMap<String, Vec<u8>>) -> (String, String) {
    let page = files.get("word/adms-page.txt").map(|b| String::from_utf8_lossy(b).into_owned()).unwrap_or_else(|| default_page().to_string());
    let mut html = String::new();
    if let Some(header) = files.get("word/header1.xml") {
        let text = word_part_html(&String::from_utf8_lossy(header));
        if !text.trim().is_empty() {
            html.push_str("<header>\n");
            html.push_str(&text);
            html.push_str("</header>\n");
        }
    }
    if let Some(document) = files.get("word/document.xml") {
        html.push_str(&word_part_html(&String::from_utf8_lossy(document)));
    }
    if let Some(footer) = files.get("word/footer1.xml") {
        let text = word_part_html(&String::from_utf8_lossy(footer));
        if !text.trim().is_empty() {
            html.push_str("<footer>\n");
            html.push_str(&text);
            html.push_str("</footer>\n");
        }
    }
    (html, page)
}

fn word_part_html(xml: &str) -> String {
    let mut html = String::new();
    let mut i = 0;
    while i < xml.len() {
        let Some(rel) = xml[i..].find('<') else { break };
        let at = i + rel;
        if word_tag(xml, at, "w:sectPr") {
            break;
        }
        if word_tag(xml, at, "w:tbl") {
            let (_, outer, next) = read_element(xml, at);
            html.push_str(&table_html(&outer));
            i = next;
            continue;
        }
        if word_tag(xml, at, "w:p") {
            let (_, outer, next) = read_element(xml, at);
            html.push_str(&paragraph_html(&outer));
            i = next;
            continue;
        }
        i = xml[at..].find('>').map(|n| at + n + 1).unwrap_or(xml.len());
    }
    html
}

fn word_tag(xml: &str, at: usize, name: &str) -> bool {
    let rest = &xml[at..];
    let open = format!("<{name}");
    if !rest.starts_with(&open) {
        return false;
    }
    matches!(rest.as_bytes().get(open.len()).copied().unwrap_or(b'>'), b'>' | b' ' | b'/')
}

fn paragraph_html(outer: &str) -> String {
    if outer.contains("<w:bottom") && !outer.contains("<w:t") {
        return "<hr/>\n".into();
    }
    let text = word_text(outer);
    let trimmed = text.trim();
    if trimmed.starts_with("{{#") || trimmed.starts_with("{{/") {
        return format!("{trimmed}\n");
    }
    if trimmed.is_empty() {
        return String::new();
    }
    let tag = if outer.contains("Heading1") {
        "h1"
    } else if outer.contains("Heading2") {
        "h2"
    } else if outer.contains("Heading3") {
        "h3"
    } else {
        "p"
    };
    format!("<{tag}>{}</{tag}>\n", xml_escape(&text).replace('\n', "<br/>"))
}

fn table_html(outer: &str) -> String {
    let mut html = String::from("<table>\n");
    let mut i = 0;
    let mut first = true;
    while i < outer.len() {
        let Some(rel) = outer[i..].find("<w:tr") else { break };
        let at = i + rel;
        if !word_tag(outer, at, "w:tr") {
            i = at + 4;
            continue;
        }
        let (_, row, next) = read_element(outer, at);
        i = next;
        let cells = row_cells(&row);
        if cells.len() == 1 && (cells[0].trim().starts_with("{{#") || cells[0].trim().starts_with("{{/")) {
            html.push_str(cells[0].trim());
            html.push('\n');
            continue;
        }
        html.push_str("<tr>");
        for cell in cells {
            let tag = if first { "th" } else { "td" };
            html.push_str(&format!("<{tag}>{}</{tag}>", xml_escape(&cell).replace('\n', "<br/>")));
        }
        html.push_str("</tr>\n");
        first = false;
    }
    html.push_str("</table>\n");
    html
}

fn row_cells(row: &str) -> Vec<String> {
    let mut cells = Vec::new();
    let mut i = 0;
    while i < row.len() {
        let Some(rel) = row[i..].find("<w:tc") else { break };
        let at = i + rel;
        if !word_tag(row, at, "w:tc") {
            i = at + 4;
            continue;
        }
        let (_, cell, next) = read_element(row, at);
        i = next;
        cells.push(word_text(&cell));
    }
    cells
}

fn word_text(xml: &str) -> String {
    let mut out = String::new();
    let mut i = 0;
    while i < xml.len() {
        if xml[i..].starts_with("<w:br") {
            out.push('\n');
            i = xml[i..].find('>').map(|n| i + n + 1).unwrap_or(xml.len());
            continue;
        }
        if xml[i..].starts_with("<w:t") {
            let gt = xml[i..].find('>').map(|n| i + n).unwrap_or(xml.len());
            let end = xml[gt..].find("</w:t>").map(|n| gt + n).unwrap_or(xml.len());
            out.push_str(&decode_entities(&xml[gt + 1..end]));
            i = end + 6;
            continue;
        }
        i += 1;
    }
    out
}

fn read_element(html: &str, start: usize) -> (String, String, usize) {
    let Some(close) = html[start..].find('>') else { return (String::new(), String::new(), html.len()) };
    let close = start + close;
    let inner = html[start + 1..close].trim();
    let body = inner.trim_end_matches('/').trim();
    let name = body.split(|c: char| c.is_whitespace()).next().unwrap_or("").trim_start_matches('/').to_lowercase();
    if inner.starts_with('/') || inner.ends_with('/') || name == "hr" || name == "br" || name == "img" || name == "col" || name == "w:br" {
        return (name, html[start..close + 1].to_string(), close + 1);
    }
    let mut depth = 1;
    let mut p = close + 1;
    while p < html.len() && depth > 0 {
        let Some(rel) = html[p..].find('<') else { break };
        let lt = p + rel;
        if html[lt..].starts_with("<!--") {
            p = html[lt..].find("-->").map(|n| lt + n + 3).unwrap_or(html.len());
            continue;
        }
        let Some(gt_rel) = html[lt..].find('>') else { break };
        let gt = lt + gt_rel;
        let tag = html[lt + 1..gt].trim();
        let tag_body = tag.trim_end_matches('/').trim();
        let tag_name = tag_body.split(|c: char| c.is_whitespace()).next().unwrap_or("").trim_start_matches('/').to_lowercase();
        let void_tag = tag.ends_with('/') || tag_name == "hr" || tag_name == "br" || tag_name == "img" || tag_name == "col" || tag_name == "w:br";
        if tag.starts_with('/') {
            if tag_name == name {
                depth -= 1;
            }
        } else if !void_tag && tag_name == name {
            depth += 1;
        }
        p = gt + 1;
    }
    (name, html[start..p.min(html.len())].to_string(), p.min(html.len()))
}

fn zip_store(files: &BTreeMap<String, Vec<u8>>) -> Vec<u8> {
    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, data) in files {
        let crc = crc32(data);
        let local_at = out.len() as u32;
        out.extend_from_slice(&0x04034b50u32.to_le_bytes());
        out.extend_from_slice(&20u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&crc.to_le_bytes());
        out.extend_from_slice(&(data.len() as u32).to_le_bytes());
        out.extend_from_slice(&(data.len() as u32).to_le_bytes());
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(data);
        central.extend_from_slice(&0x02014b50u32.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&crc.to_le_bytes());
        central.extend_from_slice(&(data.len() as u32).to_le_bytes());
        central.extend_from_slice(&(data.len() as u32).to_le_bytes());
        central.extend_from_slice(&(name.len() as u16).to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&0u32.to_le_bytes());
        central.extend_from_slice(&local_at.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }
    let cd_at = out.len() as u32;
    let cd_len = central.len() as u32;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x06054b50u32.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out.extend_from_slice(&(files.len() as u16).to_le_bytes());
    out.extend_from_slice(&(files.len() as u16).to_le_bytes());
    out.extend_from_slice(&cd_len.to_le_bytes());
    out.extend_from_slice(&cd_at.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out
}

fn unzip(bytes: &[u8]) -> Result<BTreeMap<String, Vec<u8>>, String> {
    let mut files = BTreeMap::new();
    let mut i = 0;
    while i + 30 <= bytes.len() && bytes[i..i + 4] == [0x50, 0x4b, 0x03, 0x04] {
        let method = u16::from_le_bytes([bytes[i + 8], bytes[i + 9]]);
        let size = u32::from_le_bytes(bytes[i + 18..i + 22].try_into().unwrap()) as usize;
        let name_len = u16::from_le_bytes([bytes[i + 26], bytes[i + 27]]) as usize;
        let extra_len = u16::from_le_bytes([bytes[i + 28], bytes[i + 29]]) as usize;
        let name_at = i + 30;
        let data_at = name_at + name_len + extra_len;
        if name_at + name_len > bytes.len() || data_at + size > bytes.len() {
            return Err("The Word document is incomplete.".into());
        }
        if method != 0 {
            return Err("This Word document is compressed. Open it in Kivo and save it again.".into());
        }
        let name = String::from_utf8_lossy(&bytes[name_at..name_at + name_len]).into_owned();
        files.insert(name, bytes[data_at..data_at + size].to_vec());
        i = data_at + size;
    }
    if !files.contains_key("word/document.xml") {
        return Err("This is not a Word document.".into());
    }
    Ok(files)
}

fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFFFFFFu32;
    for &b in data {
        crc ^= b as u32;
        for _ in 0..8 {
            let mask = (crc & 1).wrapping_neg();
            crc = (crc >> 1) ^ (0xEDB88320 & mask);
        }
    }
    !crc
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_letter_is_stored_as_a_word_document_and_prints_from_that_document() {
        let html = "<header><p>{{ company.name }}</p></header><h1>Proposal</h1><p>Dear {{ client.name }}</p><hr/><table><tr><th>Fee</th></tr>{{#each fee_items}}<tr><td>{{ description }}</td></tr>{{/each}}</table>";
        let css = "@page { size: A4; margin: 18mm 16mm 20mm 16mm; }\np { font-size: 11pt; }";
        let (payload, marker) = store(html, css).unwrap();
        assert_eq!(marker, "docx");
        assert!(payload.starts_with("UEsDB"));
        let (back, page) = prepare(&payload, &marker).unwrap();
        assert_eq!(back, html);
        assert!(page.contains("A4"));
        let files = unzip(&decode(&payload).unwrap()).unwrap();
        let document = String::from_utf8(files["word/document.xml"].clone()).unwrap();
        assert!(document.contains("Proposal") && document.contains("w:pgSz") && document.contains("fee_items"));
    }
}
