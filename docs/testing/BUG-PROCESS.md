# Bug process: report, document, fix, verify

## 1. Report (as soon as a bug is found)

1. **Reproduce twice.** If it does not reproduce, log it as `S?` with "intermittent" and the counts (for example 2 of 5).
2. **Take the id** from [BUG-LOG.md](BUG-LOG.md): `BUG-<nnnn>`, next free number.
3. **Add a row** to the BUG-LOG table. For S1 and S2 also create `docs/testing/bugs/BUG-<nnnn>.md` from the template below.
4. **Attach evidence** in `docs/testing/evidence/<run>/BUG-<nnnn>/`: screenshot of the window, the exact error text, the event log entry for a crash (faulting module and exception code), the command and its output. No installers, databases or personal data. Black out names and amounts from real data (there should be none: tests use test data).
5. **Do not fix it yet**, unless it blocks every following test (an S1 start-up crash). Then fix, re-run, and continue.

### Bug file template

```
# BUG-<nnnn>: <one line, what is wrong>

Severity: S1|S2|S3|S4     Status: open     Found: <date> by <agent> in run <run id>
App and version: <app> <version>, installer <file> <sha256 first 12>, commit <hash>
Test case: <ID>
Area: <area from the plan>

## Steps to reproduce
1. ...
2. ...

## Expected
(copy from the test case)

## Actual
(what happened, with the exact message)

## Evidence
- evidence/<run>/BUG-<nnnn>/...

## Notes
(workaround, how often, suspected cause if known)
```

## 2. Fix

1. **Branch** from the current default branch of the repository that owns the code: `fix/BUG-<nnnn>-<short-name>`. One bug per branch. Never commit to `main`/`master`.
2. **Find the cause**, not the symptom. Write the cause in the bug file ("Cause").
3. **Write the regression test first** where the code has a test suite (xunit in `Office.Shared.Tests`, `AQC.CadImport.Tests`, `Osas.Core.Tests`; `cargo test` crates). Watch it fail. For a UI-only bug add a UIA check to `O-COREOP\BBSDesktop\tests\e2e` or a script under the owning repository's `scripts`. If neither is possible say why in the bug file.
4. **Change the code** as little as possible. Match the style of the surrounding code (comment density, naming, idiom). Do not reformat files.
5. **Update documentation** that the bug showed to be wrong (help page, `docs/status.md`, `docs/SUITE.md` copies when names or links change; the four copies must stay identical). If the documentation was right and the code wrong, the documentation stays.
6. **Run** the owning suite and the plan section again: the failed case, then its neighbours, then the whole area.
7. **Commit** with `git add <paths>` and a message `Fix BUG-<nnnn>: <what>`; add the attribution line the repository requires. Check `git diff --cached --name-only` for build output before committing.
8. **Push the branch** and open a pull request titled `Fix BUG-<nnnn>: ...`. The description has: the bug id, cause, fix, tests added, and the case results before and after.
9. Update the row in BUG-LOG: status `fixed (PR <number>)`.

## 3. Verify and close

- A different run of the original case, on an installer built from the fix branch, gives `PASS`. Record the installer name and hash.
- The regression test is in the suite and passes in CI or locally.
- Status moves `fixed` to `verified`, then `closed` when the pull request is merged by a person.
- A bug that comes back is reopened with a link to the earlier one and gets one severity higher.

## 4. Status values

`open` found and logged; `in progress` a fix branch exists; `fixed` pull request opened; `verified` the case passes on the fix build; `closed` merged; `wontfix` with a reason a person agreed to; `cannot reproduce` after 3 tries on a clean install; `duplicate of BUG-nnnn`.

## 5. What the agent must never do

- Fix a bug without logging it, or change a test's expected value to make it pass.
- Push to a default branch, force-push, rewrite history, or delete a branch it did not create.
- Commit installers, `bin`, `obj`, `target`, `dist`, `stage`, `.osp`, `.osas`, `neon.env`, credentials or evidence containing personal data.
- Use a real company file or real project. Put a real secret into any report.
- Close a bug as verified from reading code. Verification is a re-run of the case.
