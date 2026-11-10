use super::*;
use crate::{approvals, falcon_csv, schema};

const FIXTURE: &[u8] = include_bytes!("../../../docs/contracts/falcon-csv.sample.csv");
const FALCON_ID: &str = "3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512";

fn conn() -> Connection {
    let mut c = Connection::open_in_memory().unwrap();
    c.pragma_update(None, "foreign_keys", "ON").unwrap();
    schema::migrate(&mut c).unwrap();
    c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, 'ADMS', 'u', 'Studio')", []).unwrap();
    c.execute("INSERT INTO users (username, password_hash, role) VALUES ('owner', 'x', 'owner')", []).unwrap();
    c.execute("INSERT INTO clients (name) VALUES ('Client')", []).unwrap();
    c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Demo Villa')", []).unwrap();
    c.execute("INSERT INTO projects (client_id, name) VALUES (1, 'Other')", []).unwrap();
    ensure(&mut c).unwrap();
    c
}

/// The fixture with one exact piece of text replaced (all occurrences with `all`), line ends normalised to LF.
fn edit(from: &str, to: &str) -> Vec<u8> {
    let text = String::from_utf8(FIXTURE.to_vec()).unwrap().replace("\r\n", "\n");
    assert!(text.contains(from), "fixture has no {from:?}");
    text.replace(from, to).into_bytes()
}

fn go(c: &mut Connection, bytes: &[u8]) -> ImportResult {
    import(c, 1, bytes, "demo.csv", Some(1)).unwrap()
}

fn count(c: &Connection, sql: &str) -> i64 {
    c.query_row(sql, [], |r| r.get(0)).unwrap()
}

fn has(issues: &[Issue], line: usize, text: &str) -> bool {
    issues.iter().any(|i| i.line == line && i.message.contains(text))
}

#[test]
fn the_fixture_is_a_valid_v1_file() {
    let parsed = falcon_csv::parse(FIXTURE).unwrap();
    assert_eq!((parsed.estimates.len(), parsed.measured.len(), parsed.takeoffs.len()), (5, 2, 3));
    assert_eq!(parsed.meta.project_id, FALCON_ID);
    assert_eq!((parsed.meta.drawing_revision, parsed.meta.currency.as_str(), parsed.meta.falcon.as_str()), (12, "INR", "1.4.1"));
    assert_eq!(parsed.total_amount, 266565.00);
    assert_eq!(parsed.sha256.len(), 64);
}

#[test]
fn a_valid_file_imports_as_drafts_and_approves_nothing() {
    let mut c = conn();
    let r = go(&mut c, FIXTURE);
    assert_eq!((r.added, r.changed, r.unchanged, r.refused), (10, 0, 0, 0), "{r:?}");
    assert!(!r.noop);
    assert_eq!(r.unmapped, ["BRK-010", "EXC-001", "PCC-001", "PLM-CW20", "PLM-WC01"]);

    let est = approvals::get(&c, r.estimate_approval_id.unwrap()).unwrap().unwrap();
    assert_eq!((est.kind.as_str(), est.status.as_str(), est.version, est.project_id), ("estimate", "draft", 1, 1));
    let body: Value = serde_json::from_str(&est.body).unwrap();
    assert_eq!(body["total"], 266565.0);
    assert_eq!(body["lines"].as_array().unwrap().len(), 5);
    assert_eq!(body["takeoff"].as_array().unwrap().len(), 3);
    assert_eq!(rows_of(&c, est.id, "takeoff").unwrap().len(), 3, "the priced basis is attached to the estimate version");

    let bill = approvals::get(&c, r.bill_approval_id.unwrap()).unwrap().unwrap();
    assert_eq!((bill.kind.as_str(), bill.status.as_str(), bill.version), ("running_bill", "draft", 1));
    assert_eq!(count(&c, "SELECT COUNT(*) FROM suite_approvals WHERE status <> 'draft'"), 0, "an import never approves");

    let log: String = c.query_row("SELECT detail FROM audit_log WHERE event = 'falcon_csv_import'", [], |r| r.get(0)).unwrap();
    assert!(log.contains(&r.file_sha256) && log.contains("added=10"), "{log}");
    assert_eq!(count(&c, "SELECT COUNT(*) FROM aos_falcon_import WHERE file_sha256 = (SELECT file_sha256 FROM aos_falcon_import LIMIT 1)"), 1);
}

#[test]
fn the_same_file_twice_changes_nothing_and_says_so() {
    let mut c = conn();
    go(&mut c, FIXTURE);
    let before = (count(&c, "SELECT COUNT(*) FROM suite_approvals"), count(&c, "SELECT COUNT(*) FROM aos_falcon_row"));
    let preview = preview(&c, FIXTURE, Some(1)).unwrap();
    assert!(preview.noop && preview.already_imported, "{preview:?}");
    let again = go(&mut c, FIXTURE);
    assert!(again.noop);
    assert_eq!((again.added, again.changed, again.unchanged, again.refused), (0, 0, 10, 0));
    assert_eq!((count(&c, "SELECT COUNT(*) FROM suite_approvals"), count(&c, "SELECT COUNT(*) FROM aos_falcon_row")), before);
}

#[test]
fn a_bad_file_is_refused_whole_with_line_numbers() {
    let cases: Vec<(&str, Vec<u8>, usize, &str)> = vec![
        ("wrong schema", edit("osas-falcon-csv/1", "osas-falcon-csv/2"), 2, "Unsupported schema"),
        ("no meta row", edit("osas-falcon-csv/1,meta,3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512,12,,,Demo Villa,,,,,,,,,,,,,currency=INR;exported=2026-11-08T10:00:00Z;falcon=1.4.1\n", ""), 0, "no meta row"),
        ("no total row", edit("osas-falcon-csv/1,total,3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512,12,,,,,10,,266565.00,,,,,,,,,\n", ""), 0, "no total row"),
        ("bad number", edit("42.500,310.00", "4x.500,310.00"), 3, "'quantity' is not a number"),
        ("thousands separator", edit("\"CW pipe 20 mm, fixed\",m,86.000,210.00,18060.00", "\"CW pipe 20 mm, fixed\",m,\"86,000\",210.00,18060.00"), 6, "'quantity' is not a number"),
        ("amount differs", edit("13175.00", "13176.00"), 3, "does not equal quantity x rate"),
        ("measured_to_date differs", edit("42.500,12.500,30.000", "42.500,12.600,30.000"), 8, "does not equal previous"),
        ("duplicate item code", edit("osas-falcon-csv/1,measured,3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512,12,,BRK-010,Brick masonry 230 mm,m3,,,,9.200,9.200,0.000,falcon,,,,,\n", "osas-falcon-csv/1,measured,3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512,12,,EXC-001,Excavation again,m3,,,,9.200,9.200,0.000,falcon,,,,,\n"), 9, "Duplicate measured item_code EXC-001 (first on line 8)"),
        ("wrong total amount", edit(",266565.00,", ",266566.00,"), 13, "total amount"),
        ("wrong total count", edit(",10,,266565.00,", ",11,,266565.00,"), 13, "number of data rows"),
        ("other project on a row", edit("osas-falcon-csv/1,estimate,3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512,12,Substructure,PCC-001", "osas-falcon-csv/1,estimate,aaaaaaaa-5a47-4d9b-8e21-7c6a90d4b512,12,Substructure,PCC-001"), 4, "differs from the meta row"),
        ("missing column", edit("schema,row_type,project_id,drawing_revision,package,item_code,description,unit,quantity,rate,amount,measured_to_date,this_period,previous,source,kind,tag,size,level,note", "schema,row_type,project_id,drawing_revision,package,item_code,description,unit,quantity,rate,amount,measured_to_date,this_period,previous,source,kind,tag,size,level"), 1, "missing column"),
    ];
    for (name, bytes, line, text) in cases {
        let mut c = conn();
        let r = go(&mut c, &bytes);
        assert!(r.refused > 0, "{name}: {r:?}");
        assert!(has(&r.issues, line, text), "{name}: wanted line {line} {text:?} in {:?}", r.issues);
        assert_eq!(count(&c, "SELECT COUNT(*) FROM suite_approvals"), 0, "{name}: nothing is partly imported");
        assert_eq!(count(&c, "SELECT COUNT(*) FROM aos_falcon_row"), 0, "{name}");
        assert_eq!(count(&c, "SELECT COUNT(*) FROM aos_falcon_link"), 0, "{name}");
        assert_eq!(count(&c, "SELECT COUNT(*) FROM audit_log WHERE event = 'falcon_csv_refused'"), 1, "{name}: the refusal is logged");
    }
}

#[test]
fn every_problem_in_a_file_is_listed_not_just_the_first() {
    let mut bytes = String::from_utf8(edit("13175.00", "13176.00")).unwrap();
    bytes = bytes.replace("42.500,12.500,30.000", "42.500,12.600,30.000");
    let issues = falcon_csv::parse(bytes.as_bytes()).unwrap_err();
    assert!(has(&issues, 3, "amount") && has(&issues, 8, "measured_to_date") && has(&issues, 13, "total amount"), "{issues:?}");
}

#[test]
fn unmapped_codes_are_reported_never_guessed_and_the_mapping_is_editable() {
    let mut c = conn();
    let p = preview(&c, FIXTURE, Some(1)).unwrap();
    assert_eq!(p.unmapped.len(), 5);
    map_set(&c, 1, "EXC-001", "KV-EARTH-01").unwrap();
    map_set(&c, 1, "PCC-001", "KV-CONC-04").unwrap();
    let p = preview(&c, FIXTURE, Some(1)).unwrap();
    assert_eq!(p.unmapped, ["BRK-010", "PLM-CW20", "PLM-WC01"]);
    let r = go(&mut c, FIXTURE);
    assert_eq!(r.unmapped, ["BRK-010", "PLM-CW20", "PLM-WC01"]);
    let lines = rows_of(&c, r.estimate_approval_id.unwrap(), "estimate").unwrap();
    let exc = lines.iter().find(|l| l["item_code"] == "EXC-001").unwrap();
    let brk = lines.iter().find(|l| l["item_code"] == "BRK-010").unwrap();
    assert_eq!(exc["kivo_item"], "KV-EARTH-01");
    assert!(brk["kivo_item"].is_null(), "an unmapped code is left unmapped");
    map_set(&c, 1, "EXC-001", "").unwrap();
    assert_eq!(map_list(&c).unwrap().len(), 1);
}

#[test]
fn an_approved_estimate_is_never_edited_the_import_makes_the_next_draft() {
    let mut c = conn();
    let first = go(&mut c, FIXTURE);
    let v1 = first.estimate_approval_id.unwrap();
    approvals::approve(&c, 1, v1).unwrap();
    let locked_body = approvals::get(&c, v1).unwrap().unwrap().body;

    let revised = edit("42.500,310.00,13175.00", "42.500,320.00,13600.00");
    let revised = String::from_utf8(revised).unwrap().replace(",266565.00,", ",266990.00,").into_bytes();
    let p = preview(&c, &revised, Some(1)).unwrap();
    assert_eq!(p.estimate.action, "new_version");
    assert_eq!((p.estimate.counts.changed, p.estimate.counts.unchanged), (1, 7));
    assert_eq!(p.estimate.differing.len(), 1);
    assert_eq!(p.estimate.differing[0].key, "EXC-001");
    assert_eq!((p.current_estimate_total, p.file_estimate_total), (Some(266565.0), 266990.0));

    let r = go(&mut c, &revised);
    assert_eq!((r.added, r.changed, r.refused), (0, 1, 0), "{r:?}");
    let v2 = approvals::get(&c, r.estimate_approval_id.unwrap()).unwrap().unwrap();
    assert_eq!((v2.version, v2.status.as_str(), v2.supersedes_id), (2, "draft", Some(v1)));
    let v1_now = approvals::get(&c, v1).unwrap().unwrap();
    assert_eq!((v1_now.status.as_str(), v1_now.body), ("approved_locked", locked_body), "the approved version is untouched and readable");

    // re-importing the revised file is again a no-op, and approving the draft supersedes the old one in the existing way
    assert!(go(&mut c, &revised).noop);
    assert_eq!(count(&c, "SELECT COUNT(*) FROM suite_approvals WHERE kind = 'estimate'"), 2);
    approvals::approve(&c, 1, v2.id).unwrap();
    assert_eq!(approvals::get(&c, v1).unwrap().unwrap().status, "superseded");
}

#[test]
fn a_draft_estimate_is_updated_in_place() {
    let mut c = conn();
    let first = go(&mut c, FIXTURE);
    let revised = String::from_utf8(edit("42.500,310.00,13175.00", "42.500,320.00,13600.00")).unwrap().replace(",266565.00,", ",266990.00,").into_bytes();
    let r = go(&mut c, &revised);
    assert_eq!(r.estimate_approval_id, first.estimate_approval_id);
    assert_eq!(count(&c, "SELECT COUNT(*) FROM suite_approvals WHERE kind = 'estimate'"), 1);
    let body: Value = serde_json::from_str(&approvals::get(&c, r.estimate_approval_id.unwrap()).unwrap().unwrap().body).unwrap();
    assert_eq!(body["total"], 266990.0);
}

#[test]
fn a_newer_drawing_revision_makes_a_new_estimate_revision() {
    let mut c = conn();
    let r12 = go(&mut c, FIXTURE);
    let r13_bytes = edit("d4b512,12,", "d4b512,13,");
    let p = preview(&c, &r13_bytes, Some(1)).unwrap();
    assert!(!p.refused && !p.noop, "{p:?}");
    assert_eq!(p.estimate.action, "new_version");
    let r13 = go(&mut c, &r13_bytes);
    assert_eq!(r13.drawing_revision, Some(13));
    assert_ne!(r13.estimate_approval_id, r12.estimate_approval_id);
    assert_eq!(r13.estimate_version, Some(2));
    // the unapproved r12 draft is superseded, not lost
    let old = approvals::get(&c, r12.estimate_approval_id.unwrap()).unwrap().unwrap();
    assert_eq!(old.status, "superseded");
    assert!(!old.body.is_empty());
    // the same file again is a no-op; the older revision is no longer the current one
    assert!(go(&mut c, &r13_bytes).noop);
    assert!(go(&mut c, FIXTURE).noop, "the r12 file still matches its own estimate");
    let changed_old = String::from_utf8(edit("42.500,310.00,13175.00", "42.500,320.00,13600.00")).unwrap().replace(",266565.00,", ",266990.00,").into_bytes();
    let refused = go(&mut c, &changed_old);
    assert!(refused.refused > 0 && refused.issues[0].message.contains("newer drawing revision"), "{refused:?}");
}

#[test]
fn a_bill_over_the_estimate_is_flagged_not_refused_and_measures_against_the_approved_estimate() {
    let mut c = conn();
    let first = go(&mut c, FIXTURE);
    approvals::approve(&c, 1, first.estimate_approval_id.unwrap()).unwrap();
    // BRK-010 estimate is 28.400; measure 30.000 to date
    let over = edit("9.200,9.200,0.000", "30.000,30.000,0.000");
    let p = preview(&c, &over, Some(1)).unwrap();
    assert!(p.flags.iter().any(|f| f.contains("BRK-010") && f.contains("more than the quantity") && f.contains("approved estimate v1")), "{:?}", p.flags);
    let r = go(&mut c, &over);
    assert_eq!(r.refused, 0);
    assert_eq!((r.changed, r.added, r.unchanged), (1, 0, 9), "{r:?}");
    let bill = approvals::get(&c, r.bill_approval_id.unwrap()).unwrap().unwrap();
    assert_eq!(bill.status, "draft");
    let body: Value = serde_json::from_str(&bill.body).unwrap();
    assert_eq!(body["measured_against"], "approved estimate v1");
    assert_eq!(body["estimate_approved"], true);
    let line = body["lines"].as_array().unwrap().iter().find(|l| l["item_code"] == "BRK-010").unwrap();
    assert_eq!((line["exceeds_estimate"].clone(), line["estimate_quantity"].clone()), (json!(true), json!(28.4)));
}

#[test]
fn an_approved_bill_locks_its_measurements_and_a_remeasure_makes_a_new_bill() {
    let mut c = conn();
    let first = go(&mut c, FIXTURE);
    let bill1 = first.bill_approval_id.unwrap();
    approvals::approve(&c, 1, bill1).unwrap();
    let locked = approvals::get(&c, bill1).unwrap().unwrap().body;

    // EXC-001 is re-measured: 45.0 to date, 15.0 this period
    let remeasured = edit("42.500,12.500,30.000", "45.000,15.000,30.000");
    let r = go(&mut c, &remeasured);
    assert_eq!((r.added, r.changed, r.unchanged), (0, 1, 9), "{r:?}");
    assert_ne!(r.bill_approval_id, Some(bill1));
    assert_eq!(r.bill_version, Some(2));
    let b1 = approvals::get(&c, bill1).unwrap().unwrap();
    assert_eq!((b1.status.as_str(), b1.body), ("approved_locked", locked), "the approved bill keeps what it used");
    let measured = rows_of(&c, bill1, "measured").unwrap();
    assert_eq!(measured.iter().find(|m| m["item_code"] == "EXC-001").unwrap()["measured_to_date"], 42.5);
    let body2: Value = serde_json::from_str(&approvals::get(&c, r.bill_approval_id.unwrap()).unwrap().unwrap().body).unwrap();
    assert_eq!(body2["lines"].as_array().unwrap().len(), 1, "the new bill holds the lines that changed");
    // MynaOps's previous (30) is flagged against MynaHub's last approved bill (0 for this item then 42.5)
    assert!(r.flags.iter().any(|f| f.contains("EXC-001") && f.contains("MynaOps's previous")), "{:?}", r.flags);
    assert!(go(&mut c, &remeasured).noop);
}

#[test]
fn a_draft_bill_takes_further_lines_in_place() {
    let mut c = conn();
    let first = go(&mut c, FIXTURE);
    let remeasured = edit("9.200,9.200,0.000", "12.000,12.000,0.000");
    let r = go(&mut c, &remeasured);
    assert_eq!(r.bill_approval_id, first.bill_approval_id);
    assert_eq!(count(&c, "SELECT COUNT(*) FROM suite_approvals WHERE kind = 'running_bill'"), 1);
    let body: Value = serde_json::from_str(&approvals::get(&c, r.bill_approval_id.unwrap()).unwrap().unwrap().body).unwrap();
    assert_eq!(body["lines"].as_array().unwrap().len(), 2, "the bill still holds both lines");
}

#[test]
fn the_project_link_is_kept_and_checked() {
    let mut c = conn();
    let none = import(&mut c, 1, FIXTURE, "demo.csv", None).unwrap();
    assert!(none.refused > 0 && none.issues[0].message.contains("Choose the MynaHub project"), "{none:?}");
    let p = preview(&c, FIXTURE, None).unwrap();
    assert!(p.needs_project);
    assert_eq!(p.suggested_project_id, Some(1), "matched by the project name");
    let missing = import(&mut c, 1, FIXTURE, "demo.csv", Some(99)).unwrap();
    assert!(missing.refused > 0 && missing.issues[0].message.contains("not in this company file"));
    go(&mut c, FIXTURE);
    assert_eq!(import(&mut c, 1, FIXTURE, "demo.csv", None).unwrap().refused, 0, "linked: the project is no longer needed");
    let other = import(&mut c, 1, FIXTURE, "demo.csv", Some(2)).unwrap();
    assert!(other.refused > 0 && other.issues[0].message.contains("already linked"), "{other:?}");
    assert_eq!(preview(&c, FIXTURE, None).unwrap().linked_project_id, Some(1));
}

#[test]
fn withdrawing_a_draft_leaves_nothing_dangling() {
    let mut c = conn();
    let r = go(&mut c, FIXTURE);
    approvals::withdraw(&c, 1, r.estimate_approval_id.unwrap()).unwrap();
    approvals::withdraw(&c, 1, r.bill_approval_id.unwrap()).unwrap();
    assert_eq!(count(&c, "SELECT COUNT(*) FROM aos_falcon_row"), 0);
    let again = go(&mut c, FIXTURE);
    assert_eq!((again.added, again.changed), (10, 0), "a withdrawn draft can be imported again");
}
