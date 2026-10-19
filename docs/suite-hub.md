# OSAS as the hub: one company file, one sign-in

Written 2026-10-19. Decision from the owner: the suite app (OSAS) holds the company's details and the sign-in, and the company file is chosen there, not in O-Studio. O-Studio is one app among the suite's apps; the company file is core and is linked to all of them.

## The model

```
OSAS  ── chooses the company file ── signs in once ── shows and edits the company details
  │
  ├─ launches O-Studio  ─┐  each gets: the company file path + a one-time sign-in ticket
  ├─ launches O-CoreOp  ─┤  and opens that company already signed in as the same person
  └─ launches O-Draft   ─┘
```

- **The company file** is the `.adms` file. OSAS remembers the choice (`%LOCALAPPDATA%\OSAS\suite.json`) and the apps follow it. An app started on its own, without OSAS, still works as before: it asks for a file and a sign-in.
- **The company details** (name, address, contact, PAN, GSTIN, registration, bank details, letterhead style and accent) live in the company file's profile. OSAS shows and edits them through the shared office interface, so every app sees the same details.
- **The sign-in** is the company file's own users (owner, principal, accounts, document editor, auditor). OSAS asks for the user name and password once.
- **The users** are created and managed in OSAS (decision 2026-10-19): add a user, change a role, set a password for someone who forgot theirs, stop or allow sign-in. Each change asks for the signed-in person's own password again, and the last active owner can never be removed. O-Studio's Security page no longer has a People section; it points here.

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
| `auth.ticket` `{ app }` | A signed-in, unlocked session | `{ ticket, expires_in, username }` |
| `auth.login_ticket` `{ ticket }` | A company open in this session | Signs in as the ticket's user |

## Built (2026-10-19)

| Piece | State |
|---|---|
| Engine: company details in `office-core` (`profile.rs`), tickets (`suite.rs`, migration 0030), operations on the office interface and on O-Studio's own interface | Built. 62 tests in `office-core`, 7 in `office-ffi`, and a ticket round trip in O-Studio's interface test |
| OSAS: choose the company file, sign in once, edit the company details, hand the file and a ticket to each app it starts | Built (OSAS 0.3.0) |
| O-Studio opens `OSAS_COMPANY` and redeems `OSAS_TICKET` at start | Built. Run end to end: OSAS signed in, O-Studio opened on Pulse for the company with no password typed |
| O-CoreOp does the same, then shows its company projects | Built. Run end to end the same way |
| O-Draft | Not done. It is not changed yet (see the hand-off in the OSAS repository) |

## Not done
- **Protected (encrypted) files.** A protected file opens through a decrypted working copy that only one program can hold. While OSAS is signed in to one, the apps cannot open it (they say it is already open); sign out of OSAS first, and each app then asks for the file password itself. Plain files, which most offices use, are shared by all of them.
- **Sign-out.** Signing out of OSAS does not sign out apps that are already open; they keep their own session until closed.
- **Idle lock.** OSAS's own session locks after sitting idle like any other. A ticket can then not be issued, the apps are started with the file only, and each asks for the sign-in. OSAS says so.
- **O-Studio's own startup page.** Run without OSAS it still starts on its welcome page and asks for a file.
