# AI-judge dry run 5 (31 Oct 2026, 17:45–17:53)

The same juror as dry runs 2–4, on the state after dry run 4's fixes: `ee42ba0`. Run at the user's request ("Redo the
independent ai judge"). What it found and what we did. Monday 06:00's run stays the final one.

## How it ran

As dry run 4: Claude Code 2.1.314 headless, `claude-opus-5-5`, `--setting-sources ""`, `--no-session-persistence`, no
`CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables, Read / Grep / Glob and `git log / show / diff / grep` only,
`--max-budget-usd 15`, on a fresh clone (no remote), the rules summary (`kickoff-plan.md` section 1) and a fresh
cell-by-cell dump of the financial sheet. The brief is dry run 4's with two changes: E20 low is described as it now
stands ("in the sheet and the evidence file as a robustness check", the user's one-framing, one-number decision), and
item 7 judges dry run 4's findings. 66 turns, 422 s, $4.76 at API list price (counted in E18 as an extra; the call left no
transcript). Eight of its shell commands were refused by the read-only tool set, as intended (a `dotnet build` among
them, so it ran nothing). Its full report: [`dry-run-5-juror-report.md`](dry-run-5-juror-report.md).

## Scores

| Row (weight) | 4: B at the claim | **5: B at the claim** | **5: B at the controlled measurement** | 4: A | **5: A, cross-check** |
|---|---|---|---|---|---|
| Net benefit (40) | 4 | 4 | 4 | 2 | 2 |
| ROI (10) | 5 | 5 | 5 | 3 | 3 |
| Payback (10) | 3 | 3 | 3 | 2 | 2 |
| Evidence (20) | 2 | 2 | 2 | 2 | 2 |
| Works (20) | 2 (3) | 2 (3) | 2 (3) | 2 (3) | 2 (3) |
| **Total** | **64 (72)** | **64 (72)** | **64 (72)** | **42 (46)** | **42 (46)** |

Our sheet: 84. Bracketed: Works 3 if a full Run week on real data is shown. It found the sheet's arithmetic correct, no
revenue counted as margin and no hours counted as money, and it says the team fixed every wording and arithmetic defect
dry run 4 named. It still doesn't accept €10,409 as the rate (€2.7–4.4k from E27 and E20 low), and it reads E28 against
"without the build the test ends".

## What we fixed (defects only: a bug, wrong or stale figures, missing sources)

| # in its report | Finding | Done |
|---|---|---|
| 3 C1 (new, high) | `cost-watch --inject … --test` sent its records to the test database, but the writer's sandbox is the tool database's: a test raise would land on the shelf the app reports, its audit where the app's rollback can't find it | Confirmed in the code and in the data. A test run now publishes nothing: `TestRunCostPublisher` (every call throws) and the watcher records what it would do (`test-run-not-published`, limit `test-run`); a command that publishes (`publish-*`, `resolve-holds`, `problems`, `--publish`) refuses `--test`. New test; the demo command run for real: it printed the decision and the tool database's 167 publish batches stayed 167 |
| C1, the data | Did it happen? | Yes, once, during a rehearsal (31 Oct, 07:31–07:34 UTC): six test batches, of which the first re-opened `ai-price-4-1772258mo` in the sandbox until 8 Nov and the last ended it again; its end date moved from 06:19 to 07:34 UTC, expired before and after. Nothing to roll back; disclosed in `HACKATHON.md` |
| 3 #3 | The test counts (1012) predate four commits | Rerun: 1016 passed, 1 skipped (E17, `HACKATHON.md`) |
| 3 C9 | "1–9 minutes" in two places vs E22's 0.7–9.1 | E22's figure in both |
| 3 C5, C6 | `kickoff-plan.md` still said framing was "pending", the headline "follows the organisers' answer", Evidence "rates 5" and €10.6k "clears band 5" | Dated notes: no organisers' answer is recorded; B is the user's decision (Prompts 13, 15); the sheet rates Evidence 3; the band-5 line is about €12.2k at the build's costs |
| 3 #8 | "Gjirafa's own vendors" had no source in the repo | `HACKATHON.md` names the filter and where it comes from: the declared calculator's `SourceQueries.cs:181` (imported) |
| 3 #9 | "Run week shown live" vs pressed 10 minutes before | "started ten minutes before the demo and shown in it" in the one-pager and `HACKATHON.md` |

Numbers, AI usage (E18 now $570.89 since the kickoff), the sheet (year 1: €107,021, ROI 663 %, payback 3.4 months), the
one-pager and the evidence file rebuilt.

## Left for the user

- **The margin-target rule's range (its §6 item 3).** Code checks a margin-target rule's kind, dates, one layer and a
  value between 0 and 100 %, and the solver never prices above max(regular, today's); it doesn't check how plausible the
  value is or how long the date range is. A misread "51" for "15" would raise every product under 51 % margin to its
  regular price for the whole range. The same gap applies to the person's "no depth cap" (up to 90 %) and binding goal.
  A tighter range is a business rule, so it is the user's to set.
- **The framing items, unchanged from dry run 4:** the headline rate (E27 €2,749 and E20 low €4,402 vs €10,409; C2),
  "not claimed" in `DECLARATION.md` (C3), "without the build the test ends" vs E28 (C4), the person's attended hours
  (C7), the declaration's wording (C8).
- Still open and not fixable here: the database isn't in the repository, the build transcripts aren't committed (E18),
  the kickoff rulings are recorded only by us, the score bands' source, the unsourced cost inputs (all labelled
  assumed), the Për ty reproduction failing by one point (E11).
