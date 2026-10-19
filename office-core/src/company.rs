//! The company file: one `.osas` SQLite database holds a whole company (data, documents, audit history).
//! The extension is a convention. Opening trusts content, never the name.
//! The file stays plain SQLite until the owner protects it. A protected file starts with `AOSENC1`.
use crate::schema as db;
use crate::workspace::{create_owner, AppState, Prepare, INTERNAL};
use rusqlite::{params, Connection, OpenFlags};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const EXTENSION: &str = "osas";
/// Files made by earlier builds used `.adms` or `.aorms`; they are the same format and still open, and are never renamed.
const LEGACY_EXTENSIONS: [&str; 2] = ["adms", "aorms"];
/// Internal format marker and SQLite application id. These identify files that already exist, so they are not renamed.
const FORMAT: &str = "AORMS-1";
/// SQLite `application_id` stamped into every company file ("AORM").
const APP_ID: i64 = 0x414F_524D;
const SQLITE_MAGIC: &[u8] = b"SQLite format 3\0";
const MAX_RECENT: usize = 10;

#[derive(Serialize, Clone)]
pub struct CompanySummary {
    pub name: String,
    pub path: String,
    pub uuid: String,
    /// Keeps this app's shared claim on the file for as long as the company is open. Never sent to the window.
    #[serde(skip)]
    #[allow(dead_code)]
    pub(crate) claim: Option<std::sync::Arc<Claim>>,
}

/// A shared claim on a company file: a lock on its `.aoslock` sidecar that any number of apps can hold together.
/// An app that needs the file to itself (to protect it, or to upgrade its format) takes the exclusive lock instead, which is
/// refused while any other app holds a claim.
pub struct Claim {
    _file: std::fs::File,
}

/// What validation learned about a file, without having changed it.
#[derive(Serialize, Debug)]
pub struct FileCheck {
    pub name: String,
    pub path: String,
    pub uuid: String,
    pub schema_version: usize,
    pub needs_upgrade: bool,
    /// True when the file is encrypted at rest and needs a password or recovery code to open.
    pub protected: bool,
}

pub struct Seal {
    pub envelope: crate::vault::Envelope,
    pub working: PathBuf,
    pub sealed_path: PathBuf,
    /// Held open so a second window cannot protect or open the same file. Drop releases the lock.
    #[allow(dead_code)]
    lock: Option<std::fs::File>,
}

pub struct ActiveCompany {
    pub summary: CompanySummary,
    pub seal: Option<Seal>,
}

pub fn path_str(p: &Path) -> String {
    p.to_string_lossy().to_string()
}

pub fn fail(msg: &str) -> String {
    msg.to_string()
}

/// Reads and checks a file without modifying it. Every failure names the actual problem.
pub fn validate(path: &Path) -> Result<FileCheck, String> {
    finish_interrupted(path);
    if !path.is_file() {
        return Err(fail("That file was not found."));
    }
    if crate::vault::is_protected(path) {
        let name = path.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
        return Ok(FileCheck { name, path: path_str(path), uuid: String::new(), schema_version: 0, needs_upgrade: false, protected: true });
    }
    let mut head = [0u8; 16];
    {
        use std::io::Read;
        let mut f = std::fs::File::open(path).map_err(|_| fail("That file could not be read."))?;
        if f.read_exact(&mut head).is_err() || head != *SQLITE_MAGIC {
            return Err(fail("This is not a company file."));
        }
    }
    let conn = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|_| fail("This company file could not be opened."))?;
    conn.busy_timeout(std::time::Duration::from_millis(800)).ok();
    let app_id: i64 = conn.pragma_query_value(None, "application_id", |r| r.get(0)).map_err(map_open_error)?;
    if app_id != APP_ID {
        return Err(fail("This is not a company file."));
    }
    let version: usize = conn.pragma_query_value(None, "user_version", |r| r.get(0)).map_err(map_open_error)?;
    if version > db::latest_version() {
        return Err(fail("This company file was made by a newer version of O-Studio. Update the app to open it."));
    }
    let check: String = conn.query_row("PRAGMA quick_check", [], |r| r.get(0)).map_err(|_| fail("This company file is damaged and cannot be opened."))?;
    if check != "ok" {
        return Err(fail("This company file is damaged (integrity check failed). Restore it from a backup."));
    }
    let meta: Result<(String, String, String), _> =
        conn.query_row("SELECT format, company_uuid, display_name FROM company_file WHERE id = 1", [], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)));
    let (format, uuid, name) = meta.map_err(|_| fail("This is not a company file."))?;
    if format != FORMAT {
        return Err(fail("This company file uses a format this version does not understand."));
    }
    Ok(FileCheck { name, path: path_str(path), uuid, schema_version: version, needs_upgrade: version < db::latest_version(), protected: false })
}

pub fn sidecar(path: &Path, suffix: &str) -> PathBuf {
    PathBuf::from(format!("{}.{suffix}", path.display()))
}

/// Completes or undoes a protect/close that stopped between the old file and the new bytes.
pub fn finish_interrupted(path: &Path) {
    let writing = sidecar(path, "writing");
    let held = sidecar(path, "held");
    if !path.exists() && writing.is_file() {
        let _ = std::fs::rename(&writing, path);
    }
    if path.is_file() && crate::vault::is_protected(path) && held.exists() {
        let _ = std::fs::remove_file(&held);
    } else if !path.exists() && held.is_file() {
        let _ = std::fs::rename(&held, path);
    }
}

pub fn write_synced(path: &Path, bytes: &[u8]) -> Result<(), String> {
    use std::io::Write;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let mut file = std::fs::File::create(path).map_err(|e| e.to_string())?;
    file.write_all(bytes).map_err(|e| e.to_string())?;
    file.sync_all().map_err(|e| e.to_string())?;
    Ok(())
}

pub fn swap_in(path: &Path, bytes: &[u8]) -> Result<(), String> {
    let writing = sidecar(path, "writing");
    let held = sidecar(path, "held");
    write_synced(&writing, bytes)?;
    if held.exists() {
        let _ = std::fs::remove_file(&held);
    }
    std::fs::rename(path, &held).map_err(|e| format!("The company file could not be saved: {e}"))?;
    if let Err(e) = std::fs::rename(&writing, path) {
        let _ = std::fs::rename(&held, path);
        return Err(format!("The company file could not be saved: {e}"));
    }
    if crate::vault::is_protected(path) {
        let _ = std::fs::remove_file(&held);
    }
    for suffix in ["-wal", "-shm", "-journal"] {
        let _ = std::fs::remove_file(PathBuf::from(format!("{}{suffix}", path.display())));
    }
    Ok(())
}

/// Takes this app's shared claim on a company file. Refused only while some app has the file to itself.
pub fn take_claim(path: &Path) -> Result<Claim, String> {
    let lock_path = sidecar(path, "aoslock");
    let file = std::fs::OpenOptions::new().read(true).write(true).create(true).open(&lock_path).map_err(|e| e.to_string())?;
    fs4::FileExt::try_lock_shared(&file).map_err(|_| fail("This company is being protected or upgraded by another app. Try again in a moment."))?;
    Ok(Claim { _file: file })
}

/// Takes the file to itself. Refused while any other app has the company open.
pub fn take_lock(path: &Path) -> Result<std::fs::File, String> {
    let lock_path = sidecar(path, "aoslock");
    let file = std::fs::OpenOptions::new().read(true).write(true).create(true).open(&lock_path).map_err(|e| e.to_string())?;
    fs4::FileExt::try_lock_exclusive(&file).map_err(|_| fail("This company is already open in another window or program."))?;
    Ok(file)
}

/// Removes decrypted working copies left behind when the program stopped. An open company keeps its file locked, so that copy stays.
pub fn sweep_working(app_dir: &Path) {
    let dir = app_dir.join("adms-working");
    let Ok(entries) = std::fs::read_dir(&dir) else { return };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_file() {
            let _ = std::fs::remove_file(path);
        }
    }
}

pub fn working_path(app_dir: &Path, sealed: &Path) -> PathBuf {
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};
    let mut hasher = DefaultHasher::new();
    path_str(sealed).hash(&mut hasher);
    app_dir.join("adms-working").join(format!("{:016x}.adms", hasher.finish()))
}

pub fn map_open_error(e: rusqlite::Error) -> String {
    let text = e.to_string().to_lowercase();
    if text.contains("locked") || text.contains("busy") {
        fail("This company is already open in another window or program.")
    } else {
        fail("This company file is damaged or is not a company file.")
    }
}

pub fn random_uuid(conn: &Connection) -> Result<String, String> {
    let h: String = conn.query_row("SELECT lower(hex(randomblob(16)))", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    Ok(format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32]))
}

/// Opens a company database read-write for use alongside other apps: write-ahead logging, ordinary locking, and a wait of up to five
/// seconds for another app's write to finish.
pub fn open_shared(path: &Path) -> Result<Connection, String> {
    let conn = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_WRITE).map_err(|_| fail("This company file could not be opened."))?;
    conn.busy_timeout(std::time::Duration::from_secs(5)).map_err(|e| e.to_string())?;
    conn.pragma_update(None, "foreign_keys", "ON").map_err(|e| e.to_string())?;
    conn.pragma_update(None, "journal_mode", "WAL").map_err(map_open_error)?;
    Ok(conn)
}

/// Opens a company database read-write and takes an exclusive lock on it, so a second window or program cannot use it.
/// Used for the decrypted working copy of a protected file, which only one app can hold.
pub fn open_exclusive(path: &Path) -> Result<Connection, String> {
    let mut conn = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_WRITE).map_err(|_| fail("This company file could not be opened."))?;
    conn.busy_timeout(std::time::Duration::from_millis(800)).map_err(|e| e.to_string())?;
    let locked = |e: rusqlite::Error| map_open_error(e);
    conn.pragma_update(None, "foreign_keys", "ON").map_err(|e| e.to_string())?;
    conn.pragma_update(None, "journal_mode", "WAL").map_err(locked)?;
    conn.pragma_update(None, "locking_mode", "EXCLUSIVE").map_err(locked)?;
    conn.execute_batch("BEGIN IMMEDIATE; COMMIT;").map_err(locked)?;
    conn.busy_timeout(std::time::Duration::from_secs(5)).map_err(|e| e.to_string())?;
    let _ = &mut conn;
    Ok(conn)
}

pub struct NewCompany<'a> {
    pub name: &'a str,
    pub username: &'a str,
    pub password: &'a str,
    pub confirm: &'a str,
}

/// Builds a new company file next to its final name and moves it into place only when complete and verified.
/// Returns the file path and the one-time recovery code. Never overwrites an existing file.
pub fn create_file(path: &Path, setup: &NewCompany, prepare: Prepare) -> Result<(PathBuf, String), String> {
    let name = setup.name.trim();
    if name.is_empty() || name.chars().count() > 120 {
        return Err(fail("Enter the company name (up to 120 characters)."));
    }
    let path = with_extension(path);
    let dir = path.parent().ok_or_else(|| fail("Choose a location for the company file."))?;
    if !dir.is_dir() {
        return Err(fail("That folder does not exist."));
    }
    if path.exists() {
        return Err(fail("A file with that name already exists. Choose a new name; existing files are never replaced."));
    }
    let staging = path.with_file_name(format!("{}.creating", path.file_name().unwrap().to_string_lossy()));
    cleanup(&staging);
    let built = build_staged(&staging, name, setup, prepare);
    let recovery = match built {
        Ok(r) => r,
        Err(e) => {
            cleanup(&staging);
            return Err(e);
        }
    };
    // `rename` fails rather than replacing on a destination that appeared meanwhile only on some platforms, so check again.
    if path.exists() {
        cleanup(&staging);
        return Err(fail("A file with that name already exists. Choose a new name; existing files are never replaced."));
    }
    if let Err(e) = std::fs::rename(&staging, &path) {
        cleanup(&staging);
        return Err(format!("The company file could not be saved there: {e}"));
    }
    Ok((path, recovery))
}

pub fn build_staged(staging: &Path, name: &str, setup: &NewCompany, prepare: Prepare) -> Result<String, String> {
    let recovery;
    {
        let mut conn = db::open(staging, prepare).map_err(|e| format!("The company file could not be created there: {e}"))?;
        conn.pragma_update(None, "application_id", APP_ID).map_err(|e| e.to_string())?;
        let uuid = random_uuid(&conn)?;
        let tx = conn.transaction().map_err(|e| e.to_string())?;
        tx.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, ?1, ?2, ?3)", params![FORMAT, uuid, name]).map_err(|e| e.to_string())?;
        tx.execute("UPDATE company_profile SET name = ?1 WHERE id = 1", params![name]).map_err(|e| e.to_string())?;
        recovery = create_owner(&tx, setup.username, setup.password, setup.confirm)?;
        tx.commit().map_err(|e| e.to_string())?;
        let check: String = conn.query_row("PRAGMA integrity_check", [], |r| r.get(0)).map_err(|e| e.to_string())?;
        if check != "ok" {
            return Err(fail("The new company file failed its integrity check and was discarded."));
        }
        conn.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(())).map_err(|e| e.to_string())?;
    }
    // Closing the last connection removes the sidecars; make sure none are left behind the staged file.
    for suffix in ["-wal", "-shm"] {
        let side = PathBuf::from(format!("{}{suffix}", staging.display()));
        if side.exists() {
            let _ = std::fs::remove_file(side);
        }
    }
    Ok(recovery)
}

/// Removes a half-built staging file and its sidecars. Only ever called with our own `.creating` name.
pub fn cleanup(staging: &Path) {
    if staging.to_string_lossy().ends_with(".creating") {
        for suffix in ["", "-wal", "-shm", "-journal"] {
            let _ = std::fs::remove_file(PathBuf::from(format!("{}{suffix}", staging.display())));
        }
    }
}

pub fn with_extension(path: &Path) -> PathBuf {
    match path.extension().and_then(|e| e.to_str()) {
        Some(e) if e.eq_ignore_ascii_case(EXTENSION) || LEGACY_EXTENSIONS.iter().any(|l| e.eq_ignore_ascii_case(l)) => path.to_path_buf(),
        _ => PathBuf::from(format!("{}.{EXTENSION}", path.display())),
    }
}

/// Opens an existing company so that other apps can have it open too: validates, takes a shared claim, snapshots and upgrades if the
/// file is older, and stamps the open time.
///
/// An upgrade changes the file for every app, so it needs the file to itself. If another app has it open the upgrade is refused with
/// a message to close that app first, and nothing is changed.
pub fn open_file(path: &Path, prepare: Prepare) -> Result<(Connection, CompanySummary), String> {
    let check = validate(path)?;
    let mut claim = take_claim(path)?;
    if check.needs_upgrade {
        drop(claim);
        let alone = take_lock(path).map_err(|_| fail("This company file was made by an older version and has to be upgraded. Close it in every other app, then open it here."))?;
        // Take a verified copy first. The upgrade only starts if the copy exists.
        let src = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|e| e.to_string())?;
        let copy = PathBuf::from(format!("{}.before-upgrade-v{}.bak", path.display(), check.schema_version));
        if !copy.exists() {
            src.execute("VACUUM INTO ?1", params![path_str(&copy)]).map_err(|e| format!("Could not take a safety copy before upgrading: {e}"))?;
        }
        drop(src);
        let mut conn = open_shared(path)?;
        prepare(&mut conn)?;
        drop(conn);
        drop(alone);
        claim = take_claim(path)?;
    }
    let mut conn = open_shared(path)?;
    prepare(&mut conn)?;
    conn.execute("UPDATE company_file SET last_opened_at = datetime('now') WHERE id = 1", []).map_err(|e| e.to_string())?;
    let name: String = conn.query_row("SELECT display_name FROM company_file WHERE id = 1", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let summary = CompanySummary { name, path: path_str(path), uuid: check.uuid, claim: Some(std::sync::Arc::new(claim)) };
    Ok((conn, summary))
}

// ---------------- converting data saved by the first release ----------------

/// The first release kept one database plus a separate files folder in the app data folder.
pub fn legacy_paths(app_dir: &Path) -> Option<(PathBuf, PathBuf)> {
    let db = app_dir.join("database").join("office.sqlite");
    db.is_file().then(|| (db, app_dir.join("files")))
}

pub fn embed_files(conn: &Connection, root: &Path, dir: &Path) -> Result<(), String> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Ok(()) };
    for e in rd.flatten() {
        let p = e.path();
        if p.is_dir() {
            embed_files(conn, root, &p)?;
        } else if let Ok(rel) = p.strip_prefix(root) {
            let key = rel.to_string_lossy().replace(std::path::MAIN_SEPARATOR, "/");
            let data = std::fs::read(&p).map_err(|e| e.to_string())?;
            crate::store::put(conn, &key, &data)?;
        }
    }
    Ok(())
}

/// Copies the first release's data into a new company file, moving its documents inside. The originals are left untouched.
pub fn convert_legacy(legacy_db: &Path, legacy_files: &Path, dest: &Path, prepare: Prepare) -> Result<PathBuf, String> {
    let dest = with_extension(dest);
    if dest.exists() {
        return Err(fail("A file with that name already exists. Choose a new name; existing files are never replaced."));
    }
    let staging = dest.with_file_name(format!("{}.creating", dest.file_name().unwrap().to_string_lossy()));
    cleanup(&staging);
    let built = (|| -> Result<(), String> {
        {
            let src = Connection::open_with_flags(legacy_db, OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|e| e.to_string())?;
            src.execute("VACUUM INTO ?1", params![path_str(&staging)]).map_err(|e| format!("The existing data could not be copied: {e}"))?;
        }
        let mut conn = db::open(&staging, prepare)?;
        conn.pragma_update(None, "application_id", APP_ID).map_err(|e| e.to_string())?;
        let tx = conn.transaction().map_err(|e| e.to_string())?;
        let uuid = random_uuid(&tx)?;
        let name: String = tx.query_row("SELECT name FROM company_profile WHERE id = 1", [], |r| r.get(0)).unwrap_or_default();
        let name = if name.trim().is_empty() { "My company".to_string() } else { name };
        tx.execute("INSERT OR IGNORE INTO company_file (id, format, company_uuid, display_name) VALUES (1, ?1, ?2, ?3)", params![FORMAT, uuid, name]).map_err(|e| e.to_string())?;
        embed_files(&tx, legacy_files, legacy_files)?;
        tx.commit().map_err(|e| e.to_string())?;
        let check: String = conn.query_row("PRAGMA integrity_check", [], |r| r.get(0)).map_err(|e| e.to_string())?;
        if check != "ok" {
            return Err(fail("The converted file failed its integrity check and was discarded."));
        }
        conn.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(())).map_err(|e| e.to_string())?;
        Ok(())
    })();
    for suffix in ["-wal", "-shm"] {
        let _ = std::fs::remove_file(PathBuf::from(format!("{}{suffix}", staging.display())));
    }
    if let Err(e) = built {
        cleanup(&staging);
        return Err(e);
    }
    std::fs::rename(&staging, &dest).map_err(|e| {
        cleanup(&staging);
        format!("The company file could not be saved there: {e}")
    })?;
    Ok(dest)
}

// ---------------- recent companies (shortcuts only; never business data) ----------------

#[derive(Serialize, Deserialize, Clone)]
pub struct Recent {
    pub uuid: String,
    pub name: String,
    pub path: String,
    pub last_opened: i64,
}

#[derive(Serialize)]
pub struct RecentRow {
    #[serde(flatten)]
    pub recent: Recent,
    pub exists: bool,
}

pub fn recent_file(dir: &Path) -> PathBuf {
    dir.join("recent_companies.json")
}

pub fn load_recent(dir: &Path) -> Vec<Recent> {
    std::fs::read_to_string(recent_file(dir)).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
}

pub fn save_recent(dir: &Path, list: &[Recent]) {
    if std::fs::create_dir_all(dir).is_ok() {
        if let Ok(t) = serde_json::to_string_pretty(list) {
            let _ = std::fs::write(recent_file(dir), t);
        }
    }
}

pub fn remember(dir: &Path, c: &CompanySummary) {
    let mut list = load_recent(dir);
    list.retain(|r| r.uuid != c.uuid && r.path != c.path);
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0);
    list.insert(0, Recent { uuid: c.uuid.clone(), name: c.name.clone(), path: c.path.clone(), last_opened: now });
    list.truncate(MAX_RECENT);
    save_recent(dir, &list);
}

// ---------------- lifecycle on the running app ----------------

/// A throw-away empty database used while no company is open, so nothing company-specific can be read or written.
pub fn placeholder() -> Connection {
    Connection::open_in_memory().expect("in-memory database")
}

pub fn install(state: &AppState, conn: Connection, summary: CompanySummary, seal: Option<Seal>, sign_out: bool) -> Result<(), String> {
    if sign_out {
        *state.session.lock().map_err(|_| INTERNAL)? = None;
    }
    let idle: u64 = conn
        .query_row("SELECT value FROM settings WHERE key = 'idle_secs'", [], |r| r.get::<_, String>(0))
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(crate::auth::DEFAULT_IDLE.as_secs());
    state.idle_secs.store(idle, std::sync::atomic::Ordering::Relaxed);
    *state.db.lock().map_err(|_| INTERNAL)? = conn;
    *state.company.lock().map_err(|_| INTERNAL)? = Some(ActiveCompany { summary, seal });
    Ok(())
}

pub fn close(state: &AppState) -> Result<(), String> {
    let sealed = {
        let company = state.company.lock().map_err(|_| INTERNAL)?;
        company.as_ref().and_then(|c| c.seal.as_ref().map(|s| (s.envelope.clone(), s.working.clone(), s.sealed_path.clone())))
    };
    {
        let mut db = state.db.lock().map_err(|_| INTERNAL)?;
        let _ = db.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(()));
        // Replacing the connection drops the old one, which releases the file lock.
        *db = placeholder();
    }
    if let Some((envelope, working, sealed_path)) = sealed {
        let persisted = (|| -> Result<(), String> {
            let plain = std::fs::read(&working).map_err(|e| e.to_string())?;
            let bytes = crate::vault::pack(&plain, &envelope)?;
            swap_in(&sealed_path, &bytes)
        })();
        if let Err(e) = persisted {
            if let Ok(conn) = open_exclusive(&working) {
                *state.db.lock().map_err(|_| INTERNAL)? = conn;
            }
            return Err(e);
        }
        for suffix in ["", "-wal", "-shm", "-journal"] {
            let _ = std::fs::remove_file(PathBuf::from(format!("{}{suffix}", working.display())));
        }
    }
    *state.session.lock().map_err(|_| INTERNAL)? = None;
    *state.company.lock().map_err(|_| INTERNAL)? = None;
    Ok(())
}

pub fn ensure_closed(state: &AppState) -> Result<(), String> {
    if state.company.lock().map_err(|_| INTERNAL)?.is_some() {
        return Err(fail("Close the current company first."));
    }
    Ok(())
}

pub fn active_summary(state: &AppState) -> Option<CompanySummary> {
    state.company.lock().ok().and_then(|g| g.as_ref().map(|c| c.summary.clone()))
}

/// Folder holding the active company's backups, beside the company file.
pub fn backup_dir(state: &AppState) -> Result<PathBuf, String> {
    let c = active_summary(state).ok_or_else(|| fail("No company is open."))?;
    let p = PathBuf::from(&c.path);
    let stem = p.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_else(|| "company".into());
    Ok(p.with_file_name(format!("{stem} backups")))
}

// ---------------- commands (usable before sign-in: they only act on files the user chose) ----------------

/// Opens a company into the application session. Refuses a second company until the first is closed.
pub fn open_company(state: &AppState, path: &str, secret: &str, unlock: &str) -> Result<CompanySummary, String> {
    ensure_closed(state)?;
    let file = Path::new(path);
    let (conn, summary, seal) = if crate::vault::is_protected(file) {
        open_protected(state, file, secret, unlock)?
    } else {
        let (conn, summary) = open_file(file, state.prepare)?;
        (conn, summary, None)
    };
    install(state, conn, summary.clone(), seal, true)?;
    remember(&state.app_dir, &summary);
    Ok(summary)
}

pub fn open_protected(state: &AppState, path: &Path, secret: &str, unlock: &str) -> Result<(Connection, CompanySummary, Option<Seal>), String> {
    finish_interrupted(path);
    if secret.trim().is_empty() {
        return Err(fail("This company file is protected. Enter the password or the recovery code."));
    }
    let bytes = std::fs::read(path).map_err(|_| fail("That file could not be read."))?;
    let (plain, envelope) = crate::vault::open_with(&bytes, secret, unlock)?;
    let lock = take_lock(path)?;
    let working = working_path(&state.app_dir, path);
    write_synced(&working, &plain)?;
    let version = {
        let conn = Connection::open_with_flags(&working, OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(|_| fail("This protected company file is damaged."))?;
        let app_id: i64 = conn.pragma_query_value(None, "application_id", |r| r.get(0)).map_err(|_| fail("This is not a company file."))?;
        if app_id != APP_ID {
            return Err(fail("This is not a company file."));
        }
        let version: usize = conn.pragma_query_value(None, "user_version", |r| r.get(0)).map_err(|_| fail("This protected company file is damaged."))?;
        if version > db::latest_version() {
            return Err(fail("This company file was made by a newer version of O-Studio. Update the app to open it."));
        }
        version
    };
    if version < db::latest_version() {
        let copy = PathBuf::from(format!("{}.before-upgrade-v{version}.bak", path.display()));
        if !copy.exists() {
            std::fs::copy(path, &copy).map_err(|e| format!("Could not take a safety copy before upgrading: {e}"))?;
        }
    }
    let mut conn = open_exclusive(&working)?;
    (state.prepare)(&mut conn)?;
    conn.execute("UPDATE company_file SET last_opened_at = datetime('now') WHERE id = 1", []).map_err(|e| e.to_string())?;
    let name: String = conn.query_row("SELECT display_name FROM company_file WHERE id = 1", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let uuid: String = conn.query_row("SELECT company_uuid FROM company_file WHERE id = 1", [], |r| r.get(0)).map_err(|e| e.to_string())?;
    let summary = CompanySummary { name, path: path_str(path), uuid, claim: None };
    Ok((conn, summary, Some(Seal { envelope, working, sealed_path: path.to_path_buf(), lock: Some(lock) })))
}

/// Creates a company file and its first owner, then opens it. Returns the one-time recovery code.
pub fn create_company(state: &AppState, path: &str, name: &str, username: &str, password: &str, confirm: &str) -> Result<(CompanySummary, String), String> {
    ensure_closed(state)?;
    let (file, recovery) = create_file(Path::new(path), &NewCompany { name, username, password, confirm }, state.prepare)?;
    let (conn, summary) = open_file(&file, state.prepare)?;
    install(state, conn, summary.clone(), None, true)?;
    remember(&state.app_dir, &summary);
    Ok((summary, recovery))
}



pub fn confirm_owner(state: &AppState, password: &str) -> Result<(), String> {
    let username = {
        let session = state.session.lock().map_err(|_| INTERNAL)?;
        session.as_ref().ok_or("Not signed in.")?.username.clone()
    };
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    crate::workspace::check_credentials(&conn, &username, password).map(|_| ()).map_err(|_| "Your password was not accepted.".to_string())
}

pub fn protect_open(state: &AppState, uid: i64, password: &str) -> Result<String, String> {
    let mut summary = {
        let mut company = state.company.lock().map_err(|_| INTERNAL)?;
        let current = company.as_mut().ok_or_else(|| fail("No company is open."))?;
        if current.seal.is_some() {
            return Err(fail("This company file is already protected."));
        }
        // Protecting changes the file for every app, so this app lets go of its shared claim to take the file to itself.
        current.summary.claim = None;
        current.summary.clone()
    };
    let sealed_path = PathBuf::from(&summary.path);
    let lock = match take_lock(&sealed_path) {
        Ok(lock) => lock,
        Err(_) => {
            if let Ok(claim) = take_claim(&sealed_path) {
                if let Ok(mut company) = state.company.lock() {
                    if let Some(current) = company.as_mut() {
                        current.summary.claim = Some(std::sync::Arc::new(claim));
                    }
                }
            }
            return Err(fail("This company is open in another app. Close it there, then protect it here."));
        }
    };
    summary.claim = None;
    let recovery = crate::auth::generate_recovery_code();
    let recovery_hash = crate::auth::hash_password(&recovery)?;
    let snapshot = state.app_dir.join("adms-working").join("protecting.adms");
    if snapshot.exists() {
        let _ = std::fs::remove_file(&snapshot);
    }
    if let Some(parent) = snapshot.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let old_hash: Option<String> = {
        let conn = state.db.lock().map_err(|_| INTERNAL)?;
        let old: Option<String> = conn.query_row("SELECT recovery_hash FROM users WHERE id = ?1", params![uid], |r| r.get(0)).ok();
        conn.execute("UPDATE users SET recovery_hash = ?1 WHERE id = ?2", params![recovery_hash, uid]).map_err(|e| e.to_string())?;
        let _ = conn.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |_| Ok(()));
        if let Err(e) = conn.execute("VACUUM INTO ?1", params![path_str(&snapshot)]) {
            if let Some(old) = &old {
                let _ = conn.execute("UPDATE users SET recovery_hash = ?1 WHERE id = ?2", params![old, uid]);
            }
            return Err(format!("The company file could not be protected: {e}"));
        }
        old
    };
    let revert = |old: &Option<String>| {
        if let Some(old) = old {
            if let Ok(conn) = state.db.lock() {
                let _ = conn.execute("UPDATE users SET recovery_hash = ?1 WHERE id = ?2", params![old, uid]);
            }
        }
        let _ = std::fs::remove_file(&snapshot);
    };
    let plain = match std::fs::read(&snapshot) {
        Ok(bytes) => bytes,
        Err(e) => {
            revert(&old_hash);
            return Err(e.to_string());
        }
    };
    let (sealed, envelope) = match crate::vault::seal(&plain, password, &recovery) {
        Ok(value) => value,
        Err(e) => {
            revert(&old_hash);
            return Err(e);
        }
    };
    if crate::vault::open_with(&sealed, password, "password").is_err() {
        revert(&old_hash);
        return Err(fail("The protected file could not be read back, so the company file was left unchanged."));
    }
    {
        *state.db.lock().map_err(|_| INTERNAL)? = placeholder();
    }
    if let Err(e) = swap_in(&sealed_path, &sealed) {
        if let Ok(conn) = open_exclusive(&sealed_path) {
            *state.db.lock().map_err(|_| INTERNAL)? = conn;
        }
        revert(&old_hash);
        return Err(e);
    }
    let working = working_path(&state.app_dir, &sealed_path);
    if let Err(e) = write_synced(&working, &plain) {
        *state.company.lock().map_err(|_| INTERNAL)? = None;
        let _ = std::fs::remove_file(&snapshot);
        return Err(e);
    }
    let _ = std::fs::remove_file(&snapshot);
    let conn = match open_exclusive(&working) {
        Ok(conn) => conn,
        Err(e) => {
            *state.company.lock().map_err(|_| INTERNAL)? = None;
            return Err(e);
        }
    };
    install(state, conn, summary, Some(Seal { envelope, working, sealed_path, lock: Some(lock) }), false)?;
    if let Ok(conn) = state.db.lock() {
        db::audit(&conn, Some(uid), "company_protected", "");
    }
    Ok(recovery)
}

pub fn rotate_recovery(state: &AppState, uid: i64, _password: &str) -> Result<String, String> {
    let recovery = crate::auth::generate_recovery_code();
    let recovery_hash = crate::auth::hash_password(&recovery)?;
    {
        let mut company = state.company.lock().map_err(|_| INTERNAL)?;
        if let Some(seal) = company.as_mut().and_then(|c| c.seal.as_mut()) {
            crate::vault::rewrap_recovery(&mut seal.envelope, &recovery)?;
        }
    }
    let conn = state.db.lock().map_err(|_| INTERNAL)?;
    conn.execute("UPDATE users SET recovery_hash = ?1 WHERE id = ?2", params![recovery_hash, uid]).map_err(|e| e.to_string())?;
    db::audit(&conn, Some(uid), "recovery_rotated", "");
    Ok(recovery)
}

pub fn rewrap_open_password(state: &AppState, password: &str) -> Result<(), String> {
    let mut company = state.company.lock().map_err(|_| INTERNAL)?;
    if let Some(seal) = company.as_mut().and_then(|c| c.seal.as_mut()) {
        crate::vault::rewrap_password(&mut seal.envelope, password)?;
    }
    Ok(())
}

pub fn rewrap_open_secrets(state: &AppState, password: &str, recovery: &str) -> Result<(), String> {
    let mut company = state.company.lock().map_err(|_| INTERNAL)?;
    if let Some(seal) = company.as_mut().and_then(|c| c.seal.as_mut()) {
        crate::vault::rewrap_password(&mut seal.envelope, password)?;
        crate::vault::rewrap_recovery(&mut seal.envelope, recovery)?;
    }
    Ok(())
}

pub fn protection_envelope(state: &AppState) -> Option<crate::vault::Envelope> {
    state.company.lock().ok()?.as_ref()?.seal.as_ref().map(|seal| seal.envelope.clone())
}

#[cfg(test)]
mod tests {
    use super::{NewCompany, CompanySummary};
    use std::path::{Path, PathBuf};

    fn create_file(path: &Path, setup: &NewCompany) -> Result<(PathBuf, String), String> {
        super::create_file(path, setup, crate::schema::migrate)
    }

    fn open_file(path: &Path) -> Result<(rusqlite::Connection, CompanySummary), String> {
        super::open_file(path, crate::schema::migrate)
    }

    fn convert_legacy(legacy_db: &Path, legacy_files: &Path, dest: &Path) -> Result<PathBuf, String> {
        super::convert_legacy(legacy_db, legacy_files, dest, crate::schema::migrate)
    }

    use super::*;

    fn temp_dir(tag: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("adms-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn setup<'a>(name: &'a str) -> NewCompany<'a> {
        NewCompany { name, username: "owner", password: "correct horse battery", confirm: "correct horse battery" }
    }

    #[test]
    fn creates_a_valid_self_describing_file_with_no_fake_records() {
        let d = temp_dir("create");
        let (path, recovery) = create_file(&d.join("Studio"), &setup("Studio One")).unwrap();
        assert_eq!(path.extension().unwrap(), "osas");
        assert!(recovery.len() >= 8);
        let check = validate(&path).unwrap();
        assert_eq!(check.name, "Studio One");
        assert!(!check.needs_upgrade && check.uuid.len() == 36);
        let c = Connection::open(&path).unwrap();
        for table in ["invoices", "clients", "projects", "journals", "documents", "bank_transactions"] {
            let n: i64 = c.query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |r| r.get(0)).unwrap();
            assert_eq!(n, 0, "{table} must start empty");
        }
        assert!(!d.join("Studio.adms.creating").exists(), "staging file must be gone");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn never_overwrites_and_leaves_nothing_behind_on_failure() {
        let d = temp_dir("overwrite");
        std::fs::write(d.join("Taken.adms"), b"important").unwrap();
        assert!(create_file(&d.join("Taken.adms"), &setup("X")).unwrap_err().contains("already exists"));
        assert_eq!(std::fs::read(d.join("Taken.adms")).unwrap(), b"important");
        // a bad password aborts the creation and removes the staging file
        let bad = NewCompany { name: "Y", username: "owner", password: "short", confirm: "short" };
        assert!(create_file(&d.join("Y.adms"), &bad).is_err());
        assert!(!d.join("Y.adms").exists() && !d.join("Y.adms.creating").exists());
        // unusable folder
        assert!(create_file(&d.join("nope").join("Z.adms"), &setup("Z")).is_err());
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn files_saved_with_the_earlier_extension_still_open() {
        let d = temp_dir("legacy-ext");
        let (path, _) = create_file(&d.join("Studio"), &setup("Studio")).unwrap();
        let old = d.join("Studio.aorms");
        std::fs::rename(&path, &old).unwrap();
        let (_c, summary) = open_file(&old).unwrap();
        assert_eq!(summary.name, "Studio");
        // a name already ending in the earlier extension is kept, not doubled
        assert_eq!(create_file(&d.join("Other.aorms"), &setup("Other")).unwrap().0, d.join("Other.aorms"));
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn rejects_files_that_only_look_like_company_files() {
        let d = temp_dir("reject");
        std::fs::write(d.join("random.adms"), b"this is not a database at all").unwrap();
        assert!(validate(&d.join("random.adms")).unwrap_err().contains("not a company file"));
        // a real SQLite file from another program
        let other = Connection::open(d.join("other.adms")).unwrap();
        other.execute_batch("CREATE TABLE t(x); INSERT INTO t VALUES (1);").unwrap();
        drop(other);
        let before = std::fs::read(d.join("other.adms")).unwrap();
        assert!(validate(&d.join("other.adms")).unwrap_err().contains("not a company file"));
        assert!(open_file(&d.join("other.adms")).is_err());
        assert_eq!(std::fs::read(d.join("other.adms")).unwrap(), before, "a failed open must not touch the file");
        assert!(validate(&d.join("missing.adms")).is_err());
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn rejects_corrupt_and_newer_files_without_changing_them() {
        let d = temp_dir("corrupt");
        let (path, _) = create_file(&d.join("A"), &setup("A")).unwrap();
        // newer schema
        let newer = d.join("newer.adms");
        std::fs::copy(&path, &newer).unwrap();
        {
            let c = Connection::open(&newer).unwrap();
            c.pragma_update(None, "user_version", db::latest_version() as i64 + 1).unwrap();
        }
        assert!(validate(&newer).unwrap_err().contains("newer version"));
        // damaged pages
        let damaged = d.join("damaged.adms");
        let mut bytes = std::fs::read(&path).unwrap();
        for b in bytes.iter_mut().skip(4096).take(8192) {
            *b = 0xAB;
        }
        std::fs::write(&damaged, &bytes).unwrap();
        let before = std::fs::read(&damaged).unwrap();
        assert!(open_file(&damaged).is_err());
        assert_eq!(std::fs::read(&damaged).unwrap(), before);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn two_apps_can_have_a_company_open_together_and_it_can_be_moved_when_closed() {
        let d = temp_dir("lock");
        let (path, _) = create_file(&d.join("Lock"), &setup("Lock Co")).unwrap();
        let (first, summary) = open_file(&path).unwrap();
        assert_eq!(summary.name, "Lock Co");
        let (second, second_summary) = open_file(&path).expect("a second app may open the same company");
        assert_eq!(second_summary.uuid, summary.uuid);
        drop(first);
        drop(second);
        // after closing, the file can be renamed and still opens: identity is the uuid, not the path
        let moved = d.join("Renamed.adms");
        for suffix in ["-wal", "-shm"] {
            let _ = std::fs::remove_file(PathBuf::from(format!("{}{suffix}", path.display())));
        }
        std::fs::rename(&path, &moved).unwrap();
        let (_c, again) = open_file(&moved).unwrap();
        assert_eq!(again.uuid, summary.uuid);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn what_one_app_writes_the_other_sees_and_the_change_counter_moves() {
        let d = temp_dir("sharing");
        let (path, _) = create_file(&d.join("Share"), &setup("Share Co")).unwrap();
        let (a, _keep_a) = open_file(&path).unwrap();
        let (b, _keep_b) = open_file(&path).unwrap();
        let before = crate::share::data_version(&b).unwrap().version;
        a.execute("INSERT INTO clients (name) VALUES ('Written by A')", []).unwrap();
        let seen: String = b.query_row("SELECT name FROM clients WHERE name = 'Written by A'", [], |r| r.get(0)).unwrap();
        assert_eq!(seen, "Written by A");
        assert_ne!(crate::share::data_version(&b).unwrap().version, before, "B can tell that A wrote");
        b.execute("INSERT INTO clients (name) VALUES ('Written by B')", []).unwrap();
        assert_eq!(a.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 2);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn taking_the_file_to_itself_is_refused_while_another_app_has_it_open() {
        let d = temp_dir("alone");
        let (path, _) = create_file(&d.join("Alone"), &setup("Alone Co")).unwrap();
        let (_conn, summary) = open_file(&path).unwrap();
        assert!(take_lock(&path).is_err(), "protecting or upgrading needs every other app closed");
        drop(summary);
        assert!(take_lock(&path).is_ok(), "once the claim is gone the file can be taken");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn an_older_file_is_not_upgraded_under_another_app_that_has_it_open() {
        let d = temp_dir("upgrade-shared");
        let path = d.join("Old.adms");
        {
            let mut c = Connection::open(&path).unwrap();
            db::apply_first(&mut c, 13).unwrap();
            c.pragma_update(None, "application_id", APP_ID).unwrap();
            c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, ?1, 'old-uuid', 'Old Co')", params![FORMAT]).unwrap();
        }
        let holder = take_claim(&path).unwrap();
        let refused = open_file(&path).err().expect("refused");
        assert!(refused.contains("upgraded") && refused.contains("Close"), "{refused}");
        assert_eq!(validate(&path).unwrap().schema_version, 13, "nothing was changed");
        drop(holder);
        let (c, _keep) = open_file(&path).unwrap();
        assert_eq!(c.pragma_query_value(None, "user_version", |r| r.get::<_, usize>(0)).unwrap(), db::latest_version());
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn protecting_is_refused_while_another_app_has_the_company_open() {
        let d = temp_dir("protect-shared");
        let (path, _) = create_file(&d.join("Studio"), &setup("Studio One")).unwrap();
        let state = AppState::new(placeholder(), d.clone(), crate::schema::migrate);
        open_company(&state, path.to_str().unwrap(), "", "").unwrap();
        let (_other, _other_summary) = open_file(&path).unwrap();
        let owner_id: i64 = state.db.lock().unwrap().query_row("SELECT id FROM users WHERE username = 'owner'", [], |r| r.get(0)).unwrap();
        let error = protect_open(&state, owner_id, "correct horse battery").unwrap_err();
        assert!(error.contains("open in another app"), "{error}");
        assert!(!crate::vault::is_protected(&path), "the file is still plain");
        close(&state).unwrap();
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn upgrading_an_older_file_takes_a_safety_copy_first() {
        let d = temp_dir("upgrade");
        // A company file exactly as the release before the newest schema left it: schema 13 (the first with the company-file tables).
        let path = d.join("Old.adms");
        {
            let mut c = Connection::open(&path).unwrap();
            db::apply_first(&mut c, 13).unwrap();
            c.pragma_update(None, "application_id", APP_ID).unwrap();
            c.execute("INSERT INTO company_file (id, format, company_uuid, display_name) VALUES (1, ?1, 'old-uuid', 'Old Co')", params![FORMAT]).unwrap();
            c.execute("INSERT INTO clients (name) VALUES ('Kept Client')", []).unwrap();
        }
        let check = validate(&path).unwrap();
        assert!(check.needs_upgrade && check.schema_version == 13);
        let (c, _) = open_file(&path).unwrap();
        assert!(d.join("Old.adms.before-upgrade-v13.bak").is_file(), "a safety copy is taken before upgrading");
        assert_eq!(c.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 1, "existing data survives the upgrade");
        // the safety copy itself is the old schema and untouched
        let bak = Connection::open(d.join("Old.adms.before-upgrade-v13.bak")).unwrap();
        assert_eq!(bak.pragma_query_value(None, "user_version", |r| r.get::<_, i64>(0)).unwrap(), 13);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn closing_a_company_leaves_no_data_and_releases_the_file() {
        let d = temp_dir("close");
        let (path, _) = create_file(&d.join("Iso"), &setup("Iso Co")).unwrap();
        let (conn, summary) = open_file(&path).unwrap();
        let state = AppState::new(placeholder(), d.clone(), crate::schema::migrate);
        install(&state, conn, summary, None, true).unwrap();
        state.db.lock().unwrap().execute("INSERT INTO clients (name) VALUES ('Secret Client')", []).unwrap();
        close(&state).unwrap();
        assert!(active_summary(&state).is_none());
        assert!(state.session.lock().unwrap().is_none());
        assert!(state.db.lock().unwrap().query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).is_err(), "no company tables may be reachable after closing");
        // the file is free again and company B starts with none of A's records
        let (pathb, _) = create_file(&d.join("Other"), &setup("Other Co")).unwrap();
        let (b, _) = open_file(&pathb).unwrap();
        assert_eq!(b.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 0);
        drop(b);
        let (a, _) = open_file(&path).unwrap();
        assert_eq!(a.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn first_release_data_converts_with_its_documents_inside() {
        let d = temp_dir("legacy");
        let legacy_db = d.join("database").join("office.sqlite");
        std::fs::create_dir_all(legacy_db.parent().unwrap()).unwrap();
        {
            let mut c = Connection::open(&legacy_db).unwrap();
            db::apply_first(&mut c, 12).unwrap();
            c.execute("INSERT INTO clients (name) VALUES ('Old Client')", []).unwrap();
            c.execute("UPDATE company_profile SET name = 'Legacy Studio'", []).unwrap();
        }
        std::fs::create_dir_all(d.join("files").join("issued-pdfs")).unwrap();
        std::fs::write(d.join("files").join("issued-pdfs").join("INV_1.pdf"), b"%PDF-old").unwrap();
        assert_eq!(legacy_paths(&d).unwrap().0, legacy_db);
        let out = convert_legacy(&legacy_db, &d.join("files"), &d.join("Legacy")).unwrap();
        let (conn, summary) = open_file(&out).unwrap();
        assert_eq!(summary.name, "Legacy Studio");
        assert_eq!(conn.query_row("SELECT COUNT(*) FROM clients", [], |r| r.get::<_, i64>(0)).unwrap(), 1);
        assert_eq!(crate::store::get(&conn, "issued-pdfs/INV_1.pdf").unwrap(), b"%PDF-old");
        assert!(legacy_db.is_file(), "the original data is never removed");
        assert!(convert_legacy(&legacy_db, &d.join("files"), &out).is_err(), "no overwrite");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn recent_list_is_shortcuts_only_and_deduplicated() {
        let d = temp_dir("recent");
        let a = CompanySummary { name: "A".into(), path: "C:/a.adms".into(), uuid: "u1".into(), claim: None };
        remember(&d, &a);
        remember(&d, &CompanySummary { path: "D:/moved/a.adms".into(), ..a.clone() });
        let list = load_recent(&d);
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].path, "D:/moved/a.adms");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn a_protected_file_opens_with_the_password_or_the_recovery_code() {
        let d = temp_dir("protect");
        let (path, _) = create_file(&d.join("Studio"), &setup("Studio One")).unwrap();
        let state = AppState::new(placeholder(), d.clone(), crate::schema::migrate);
        open_company(&state, path.to_str().unwrap(), "", "").unwrap();
        let owner_id: i64 = state.db.lock().unwrap().query_row("SELECT id FROM users WHERE username = 'owner'", [], |r| r.get(0)).unwrap();
        *state.session.lock().unwrap() = Some(crate::auth::Session {
            user_id: owner_id,
            username: "owner".into(),
            role: crate::auth::Role::Owner,
            locked: false,
            last_activity: std::time::Instant::now(),
        });
        let uid = crate::workspace::require(&state, crate::auth::Permission::ManageSettings).unwrap();
        confirm_owner(&state, "correct horse battery").unwrap();
        let code = protect_open(&state, uid, "correct horse battery").unwrap();
        assert!(crate::vault::is_protected(&path));
        assert!(!std::fs::read(&path).unwrap().starts_with(b"SQLite format 3"));
        close(&state).unwrap();
        match open_company(&state, path.to_str().unwrap(), "", "") {
            Err(error) => assert!(error.contains("protected"), "{error}"),
            Ok(_) => panic!("a protected file opened without a secret"),
        }
        let summary = open_company(&state, path.to_str().unwrap(), "correct horse battery", "password").unwrap();
        assert_eq!(summary.name, "Studio One");
        close(&state).unwrap();
        let summary = open_company(&state, path.to_str().unwrap(), &code, "recovery").unwrap();
        assert_eq!(summary.name, "Studio One");
        close(&state).unwrap();
        let (plain, _) = create_file(&d.join("Plain"), &setup("Plain")).unwrap();
        assert_eq!(open_company(&state, plain.to_str().unwrap(), "", "").unwrap().name, "Plain");
        close(&state).unwrap();
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn new_files_are_osas_and_earlier_names_are_kept() {
        assert_eq!(with_extension(Path::new("D:/Firm/Acme")), PathBuf::from("D:/Firm/Acme.osas"));
        for name in ["Acme.osas", "Acme.OSAS", "Acme.adms", "Acme.aorms"] {
            assert_eq!(with_extension(Path::new(name)), PathBuf::from(name), "{name} must not be renamed");
        }
    }

    #[test]
    fn a_file_made_with_the_earlier_adms_name_still_opens() {
        let d = temp_dir("legacy-name");
        let (path, _) = create_file(&d.join("Old"), &setup("Old Co")).unwrap();
        let old = d.join("Old Company.adms");
        std::fs::rename(&path, &old).unwrap();
        assert_eq!(validate(&old).unwrap().name, "Old Co");
        let _ = std::fs::remove_dir_all(&d);
    }
}
