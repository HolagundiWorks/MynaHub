# Studio as the hub: one company file, one sign-in

> **Partly retired 2026-11-08.** The one-time sign-in ticket and the company path passed to Falcon and Tren no longer exist: Tren and Falcon are independent of Kivo and share only the local `.osp`. See [SUITE.md](SUITE.md). The rest is kept as history.

Written 2026-10-19; **updated 2026-10-28** for the Studio↔OSAS merge ([studio-osas-merge.md](studio-osas-merge.md)).

**Decision (2026-10-28):** **Kivo** is the user-facing suite shell. It holds the company details, the admin password, users, and Myna cards. The suite brand remains **OSAS**; env vars and `suite.json` keep the `OSAS_*` names so CoreOp, Draft and Form do not need a rename. A separate OSAS.exe hub is retired (2026-11-01) once Studio ships these duties.

## The model

```
Launcher  ── chooses/creates the company file ── signs in once ──▶ Kivo: edits company details + users
  │
  ├─ card: Falcon  ─┐  each gets: the company file path + a one-time sign-in ticket
  ├─ card: Tren   ─┤  and opens that company already signed in as the same person
  └─ card: Finch    ─┘
```

- **The company file** is the `.osas` file (earlier `.adms` files open too). It is **chosen and created in Studio** (merge decision 2026-10-28). Studio remembers the choice (`%LOCALAPPDATA%\OSAS\suite.json`) so sibling apps can follow it. CoreOp/Draft started alone still redeem `OSAS_COMPANY` / `OSAS_TICKET` or offer to open the remembered company.
- **The company details** (name, address, contact, PAN, GSTIN, registration, bank details, letterhead style and accent) live in the company file's profile. Studio shows and edits them; every app reads the same details.
- **The sign-in** is the company file's own users (owner, principal, accounts, document editor, auditor). Studio asks for the user name and password once (admin password stays here only).
- **The users** are created and managed in Studio: add a user, change a role, set a password for someone who forgot theirs, stop or allow sign-in. Each change asks for the signed-in person's own password again, and the last active owner can never be removed.

## Single sign-in without storing passwords

OSAS never stores or passes the password. After it has signed in, it asks the engine for a **suite ticket** for each app it is about to launch:

| Property | Rule |
|---|---|
| Who can ask | A signed-in, unlocked session. Asking counts as activity. |
| What it is | 32 random bytes (64 hex characters). Only its SHA-256 is stored, in `suite_tickets`. |
| Lifetime | 120 seconds, one use. |
| Who it signs in | The user who asked, with that user's role. No more, no less. |
| Handed over | In an environment variable of the app OSAS starts (`OSAS_TICKET`), with the file path in `OSAS_COMPANY`. Not on the command line, which other programs can list. |
| Refused when | Used already, expired, the user is disabled or gone, or no company is open in the redeeming app. |
| Audit | `suite_ticket_issued` (app) and `login_ticket_ok` (app) are written to the company file's audit log. |

The ticket proves that a person who knew the password signed in a moment ago on this computer. It does not let anyone sign in later or elsewhere. Anything running as the same Windows user could read an environment variable of a child process it starts, but such a program could equally read the app's memory; the ticket does not widen that.

When an app is started by OSAS without a valid ticket (it expired, or the person signed out), it falls back to its normal sign-in page with the company already chosen.

## Operations

| Operation | Needs | Does |
|---|---|---|
| `company_profile_get` | View | The company details |
| `company_profile_save` | Manage settings | Saves them (audited) |
| `user_list`, `user_create`, `user_update`, `user_set_password` | Manage users (each change also needs `admin_password`) | The company's users |


## Who owns what (decision 2026-10-19)

| Data | Owned and edited in | Read by |
|---|---|---|
| **Company details**: name, address, registrations, bank, letterhead, accent | **Kivo** | every app, for reports, title plates, letters, invoices |
| **Users and sign-in** | **Kivo** | every app |
| **Project details**: code, name, site, client, service, floors, brief | **Kivo** | Falcon and Tren, for reports, title plates and other documents |
| Quantities, estimates, billing | Falcon (full data in `.osp`; **abstracts** in company `aqc.project` when linked) | Kivo dashboards, fee proposals, Approvals |
| Drawings and take-off geometry | Tren (records in the `.osp`) | Falcon reads the take-off |
| **The AI assistant** | **Studio hosts it** (was OSAS) | it reads the other apps through the suite's own connection |

The apps keep no copy of the company or project details. A document is made from one read of the company file, `document_context { project_id? }` (permission: view). It returns the company profile, today's date in the file's clock, and, when a project is named, the project, its client, its brief, the Council of Architecture service in words, and its `ProjectRef`. A change in OSAS or Kivo shows in the next document any app makes. Apps must not edit what they only read: Falcon and Tren do not save company or project details.

**Status.** The engine call `document_context` is built and tested (4 tests). Kivo already keeps projects in the company file. **Falcon** now reads it (2026-10-19): for a project linked to the company file, the company name, address, contacts, GSTIN and PAN come from OSAS, and the project name, site and client from Kivo, on opening, on reload, and whenever another app changes the file; the PM party on reports follows the company; those Settings fields are read-only and say where to change them. CIN, the logo and the signatory stay in Falcon. A project not linked to the company file keeps using its own saved details. Run in the app: not yet (it builds and the library is tested). **Tren** (branch `feat/o-draft-company-file`, 2026-10-19, not merged): `aad-office` loads `office.dll` and the `companyfile` command sets the drawing's project details from it; `bplttitleblock` prints the company, client, site and date. No project chooser or automatic refresh yet; run in the installed app: not yet.

**The assistant runs in the suite.** OSAS, not a separate program the user configures, would host the assistant: it opens the company with the signed-in person's own session, so it can use only what that person's role allows, and its tools are the governed, read-only tools in `office-agent` (and later `aqc.*`, `cad.*`). Changes would be proposals the person approves, written to the audit log. Built 2026-10-19: the assistant inside OSAS, on Claude Sonnet 5.5 by default (Haiku 5.5 and Opus 5.5 selectable), using the person's own API key kept encrypted for their Windows account, and only after they agree to send their questions and the records looked up to Anthropic. It uses the engine operations `agent_tools` and `agent_call`, which run the read-only tools of `office-agent` as the signed-in person and write the audit entry. The tool `company.details` leaves out bank and UPI details. Not yet run against the live service.

## Built (2026-10-19)

| Piece | State |
|---|---|
| Engine: company details in `office-core` (`profile.rs`), tickets (`suite.rs`, migration 0030), operations on the office interface and on Kivo's own interface | Built. 62 tests in `office-core`, 7 in `office-ffi`, and a ticket round trip in Kivo's interface test |
| Launcher: choose/create the company file and sign in once. Studio: edit company details and users, write `suite.json`, launch sibling apps with tickets. Studio no longer chooses or creates companies (2026-11-03) | **Implemented** in Myna and Kivo. Separate OSAS.exe hub retired ([osas-deprecation.md](osas-deprecation.md)) |
| Approvals inbox (CoreOp draft → Studio approve → lock → revise) | **Implemented** (`docs/contracts/approvals.md`, Practice → Approvals) |
| Assistant (governed `agent_tools` / `agent_call`, consent prefs) | **Implemented** local tool runner in Studio (Office → Assistant). Cloud Anthropic chat UI still thin vs the old OSAS page |
| Kivo opens `OSAS_COMPANY` and redeems `OSAS_TICKET` at start | Built |
| Falcon does the same, then shows its company projects | Built |
| Tren | Ticket/companyfile path in progress in Draft; not blocked by this merge |

## Not done
- **Protected (encrypted) files.** A protected file opens through a decrypted working copy that only one program can hold. While OSAS is signed in to one, the apps cannot open it (they say it is already open); sign out of OSAS first, and each app then asks for the file password itself. Plain files, which most offices use, are shared by all of them.
- **Sign-out.** Signing out of OSAS does not sign out apps that are already open; they keep their own session until closed.
- **Idle lock.** OSAS's own session locks after sitting idle like any other. A ticket can then not be issued, the apps are started with the file only, and each asks for the sign-in. OSAS says so.
- **Standalone start.** Run without OSAS, an app has no file picker: it offers to start OSAS, or to open the company OSAS last chose and sign in there.
- **Switching company while apps are open.** An app keeps the company it opened until it is closed; choosing another in OSAS does not move apps that are already open.
- **Earlier-version data.** Kivo still offers to convert data from the first release (a one-off migration) on its start screen.
