# Run report template

Copy to `docs/testing/runs/<yyyy-mm-dd>-<app>.md` and fill it in. One report per app per run. Keep it factual.

```
# Test run: <app> <yyyy-mm-dd>

Agent: <name>     Started: <time>     Finished: <time>
Plan: PLAN-<APP>.md at commit <hash of the docs>

## Build under test
| Item | Value |
|---|---|
| Installer | <file name> |
| SHA-256 | <hash> |
| Repository commits | Kivo <hash>, Falcon <hash>, Tren <hash>, o-ui <hash> |
| Windows | <build number> |
| Baseline (ENV-01..05) | <pass/fail with counts> |

## Summary
| Result | Count |
|---|---|
| PASS | |
| FAIL | |
| BLOCKED | |
| NOT RUN | |
| N/A | |

Bugs found: <ids by severity>     Bugs fixed and verified: <ids>     Open S1/S2: <ids or none>

## Results
| Case | Result | Observed (for FAIL, BLOCKED, NOT RUN: why) | Evidence |
|---|---|---|---|
| <ID> | PASS | <what was seen, with the number for calculation cases> | <file> |

## Calculations (money and quantities)
| Case | Hand calculation | App result | Difference |
|---|---|---|---|

## Not covered
(everything that was not run and why; features found in the app that the plan does not list)

## Documentation problems
(help text or docs that do not match the app: file, section, what to change)

## Notes for the next run
```

Rules: every row of the plan appears here (even `NOT RUN`). A `PASS` has an "Observed" entry. A calculation case has a row in the calculation table. The summary counts add up to the number of cases in the plan.
