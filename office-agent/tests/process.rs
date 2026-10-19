//! Runs the real `office-mcp` program the way an AI assistant's host would: a company file on disk, the password in the environment,
//! and JSON-RPC lines on standard input and output.

use office_core::company;
use office_core::workspace::AppState;
use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};

#[test]
fn the_program_serves_the_office_tools_over_standard_input_and_output() {
    let dir = std::env::temp_dir().join(format!("office-mcp-process-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let file = dir.join("Studio.osas");
    {
        let state = AppState::new(company::placeholder(), dir.clone(), office_core::schema::migrate);
        company::create_company(&state, &dir.join("Studio").to_string_lossy(), "Studio", "owner", "correct horse battery", "correct horse battery").unwrap();
        state.db.lock().unwrap().execute("INSERT INTO clients (name, city) VALUES ('Rao family', 'Pune')", []).unwrap();
        company::close(&state).unwrap();
    }

    // wrong password: it refuses to start and says why
    let refused = Command::new(env!("CARGO_BIN_EXE_office-mcp"))
        .args(["--company", &file.to_string_lossy(), "--user", "owner"])
        .env("OFFICE_MCP_PASSWORD", "not the password")
        .stdin(Stdio::null())
        .output()
        .unwrap();
    assert!(!refused.status.success());
    assert!(String::from_utf8_lossy(&refused.stderr).contains("Invalid username or password"));

    let mut child = Command::new(env!("CARGO_BIN_EXE_office-mcp"))
        .args(["--company", &file.to_string_lossy(), "--user", "owner"])
        .env("OFFICE_MCP_PASSWORD", "correct horse battery")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    let mut output = BufReader::new(child.stdout.take().unwrap());
    let mut ask = |request: Value| -> Value {
        writeln!(input, "{request}").unwrap();
        input.flush().unwrap();
        let mut line = String::new();
        output.read_line(&mut line).unwrap();
        serde_json::from_str(&line).unwrap()
    };
    assert_eq!(ask(json!({ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {} }))["result"]["serverInfo"]["name"], "office-mcp");
    assert!(ask(json!({ "jsonrpc": "2.0", "id": 2, "method": "tools/list" }))["result"]["tools"].as_array().unwrap().len() >= 8);
    let run = ask(json!({ "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "clients.search", "arguments": { "query": "pune" } } }));
    let body: Value = serde_json::from_str(run["result"]["content"][0]["text"].as_str().unwrap()).unwrap();
    assert_eq!(body["data"][0]["name"], "Rao family");
    drop(input);
    assert!(child.wait().unwrap().success(), "closing the input ends the program cleanly");
    // and the file is released, so the office app can open it again
    assert!(company::open_file(&file, office_core::schema::migrate).is_ok());
    let _ = std::fs::remove_dir_all(&dir);
}
