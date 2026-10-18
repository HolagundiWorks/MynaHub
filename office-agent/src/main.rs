//! `office-mcp`: lets an AI assistant read the office records through a fixed set of tools.
//!
//! ```text
//! office-mcp --company "D:\Studio\Studio.adms" --user owner
//! ```
//!
//! The password is read from the environment variable `OFFICE_MCP_PASSWORD`, because the assistant talks to this program on standard
//! input and output and a prompt would collide with it. Only one program can have a company file open at a time, so close the office
//! app first. The assistant can use only the tools in `office_agent::TOOLS`, and only as the user named here.
//!
//! Example client configuration (Claude Desktop or Claude Code):
//! `{ "mcpServers": { "office": { "command": "office-mcp", "args": ["--company", "D:\\Studio\\Studio.adms", "--user", "owner"], "env": { "OFFICE_MCP_PASSWORD": "..." } } } }`

use office_core::workspace::{self, AppState};
use office_core::company;
use std::io::{BufRead, Write};

fn argument(name: &str) -> Option<String> {
    let args: Vec<String> = std::env::args().collect();
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned()
}

fn main() {
    let (Some(path), Some(user)) = (argument("--company"), argument("--user")) else {
        eprintln!("Usage: office-mcp --company <file.adms> --user <name>   (password in OFFICE_MCP_PASSWORD)");
        std::process::exit(2);
    };
    let Ok(password) = std::env::var("OFFICE_MCP_PASSWORD") else {
        eprintln!("Set OFFICE_MCP_PASSWORD to the user's password.");
        std::process::exit(2);
    };
    let app_dir = std::env::temp_dir().join("office-mcp");
    let state = AppState::new(company::placeholder(), app_dir, office_core::schema::migrate);
    if let Err(why) = company::open_company(&state, &path, "", "password") {
        eprintln!("{why}");
        std::process::exit(1);
    }
    if let Err(why) = workspace::login(&state, &user, &password) {
        eprintln!("{why}");
        std::process::exit(1);
    }
    // an idle session locks itself; sign in again with the same credentials so a long conversation keeps working
    let keep_signed_in = |state: &AppState| {
        let locked = workspace::read_auth_status(state).map(|s| s.locked || s.user.is_none()).unwrap_or(true);
        if locked {
            let _ = workspace::logout(state);
            let _ = workspace::login(state, &user, &password);
        }
    };
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        if let Some(answer) = office_agent::mcp::handle(&state, &line, &keep_signed_in) {
            if writeln!(stdout, "{answer}").is_err() || stdout.flush().is_err() {
                break;
            }
        }
    }
    let _ = company::close(&state);
}
