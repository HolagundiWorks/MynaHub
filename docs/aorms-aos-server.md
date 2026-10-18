# What AORMS needs so that O-Studio can have its own session

O-Studio signs in the same way O-CoreOp and O-Draft do (contract `2026-10-aqc-1`) and starts its own session with `POST /api/aqc/v1/session` and `app: "aos"`. The AORMS server accepts only `"aqc"` and `"aad"` today, so O-Studio gets a refusal on that step until the server is changed. The change is in the AORMS repository and is not made here.

Needed on the AORMS side:
1. `web/lib/aqc/contract.ts`: add `"aos"` to `AQC_APPS`.
2. The sessions table (`aqc_sessions`, keyed by account and client app, migration 0111): allow `aos` as a client app value, if a check constraint limits it, and add a migration.
3. `entitlement`: decide whether O-Studio needs a plan (the contract says the login is the licence, Studio plan or higher).
4. Update `AORMS-SUITE-SIGNIN.md`: O-Studio is a third desktop app, with its session entry at `AORMS/Connect/aos`.

Until then the O-Studio client is complete and tested against a stand-in server (`aorms-connect/src/tests.rs`), but it has not been run against the real portal.
