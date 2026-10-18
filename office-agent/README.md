# office-agent

A governed set of read-only tools over the office records, and an MCP server (`office-mcp`) so an AI assistant can use them.

The assistant never touches the database. It names a tool and gives arguments. This crate checks the arguments against the tool's schema (unknown arguments are refused), checks that the signed-in user may use the tool, runs a fixed operation, and writes the call or the refusal to the audit log. Results are marked untrusted, because records can contain text typed by anyone, and are cut to 200 items.

## Tools
`clients.search`, `projects.list`, `projects.brief`, `library.search`, `staff.list`, `proposals.list`, `meetings.list`, and `finance.tds26as_check` (needs finance access). None of them changes anything. There is no SQL tool and no file access.

## Running it
```
office-mcp --company "D:\Studio\Studio.adms" --user owner
```
The password comes from the environment variable `OFFICE_MCP_PASSWORD`, because the assistant uses standard input and output. Only one program can have a company file open at a time, so close the office app first. The assistant acts as the user named, with that user's role.

Client configuration (Claude Desktop or Claude Code):
```json
{ "mcpServers": { "office": { "command": "office-mcp", "args": ["--company", "D:\Studio\Studio.adms", "--user", "owner"], "env": { "OFFICE_MCP_PASSWORD": "..." } } } }
```
Use a user with the lowest role that does the job. The auditor role is read-only, and the document editor cannot see finance tools.

## Not built
Tools that change records (they would return a preview and need an approval step), tools for O-Draft and O-CoreOp, and the suite host that joins them. See `docs/suite-architecture.md`.
