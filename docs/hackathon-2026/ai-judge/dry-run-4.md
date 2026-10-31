# AI-judge dry run 4 (31 Oct 2026, 17:26–17:35)

The same juror as dry runs 2, 3 and 3b, on the state after the app redesign, the campaign work, the evidence file and the
AI Pioneer meter: `e86aeff`. Run unattended while the user was away. What it found and what we did. Monday 06:00's run
stays the final one.

## How it ran

The same isolation as before: Claude Code 2.1.314 headless, `claude-opus-5-5`, `--setting-sources ""` (so neither the
meter's hooks nor any memory or instructions reach it), `--no-session-persistence`, no `CLAUDE*` / `ANTHROPIC_*` / `MCP_*`
variables, Read / Grep / Glob and `git log / show / diff / grep` only, `--max-budget-usd 15`. It read a fresh clone (no
remote), the rules summary (`kickoff-plan.md` section 1) and a fresh cell-by-cell dump of the financial sheet. The brief is
3b's: the claim is framing B at its headline (B0 €10,409 a month, €12,965 at full scope) with the controlled €4,402 as its
floor and framing A as the cross-check; B scored at the claim and at the controlled measurement; whether it accepts
€10,409 and what would change its mind; items 2–6 as before (now naming `evidence.html`); item 7 judges 3b's findings
without taking our answers on trust. 62 turns, 491 s, $4.84 at API list price (counted in E18 as an extra; the call left
no transcript). Six of its shell commands were refused by the read-only tool set, as intended. Its full report:
[`dry-run-4-juror-report.md`](dry-run-4-juror-report.md).

## Scores

| Row (weight) | Dry run 3b: B at the claim | **4: B at the claim** | **4: B at the controlled measurement** | 3b: A | **4: A, cross-check** |
|---|---|---|---|---|---|
| Net benefit (40) | 4 | 4 | 4 | 2 | 2 |
| ROI (10) | 5 | 5 | 5 | 3 | 3 |
| Payback (10) | 3 | 3 | 3 | 2 | 2 |
| Evidence (20) | 2 | 2 | 2 | 2 | 2 |
| Works (20) | 2 (3) | 2 (3) | 2 (3) | 2 (3) | 2 (3) |
| **Total** | **64 (72)** | **64 (72)** | **64 (72)** | **42 (46)** | **42 (46)** |

Our sheet: B · Mid 84. Bracketed: Works 3 if a full Run week on real data is shown live. It doesn't accept €10,409 as the
rate: "a defensible rate from this repository is about €2.7–4.4k a month", resting on E27's €2,749 (products that sold in
both windows) and E20 low's €4,402; and it reads E28 (four Kosovo runs and full MK/AL pushes in October) as contradicting
"without the build the test ends".

## What we fixed (defects only: wrong figures, missing disclosures, broken steps, a bug)

| # in its report | Finding | Done |
|---|---|---|
| 1 §2, 3 #11 | Prompt 15's footnote (E27: €11.8k vs untouched, €2.7k on products that sold in both windows, September €3.9–4.3k) was never written into the one-pager or HACKATHON.md, which still called E20 and E27 "robustness checks" | Prompt 15's sentence is in both (generated from E27 in the one-pager). The "robustness checks" label and the single B0 stay: the user's decision (Prompt 13, guarded by a test); E20 low stays in the evidence file and the sheet |
| 3 #14 | HACKATHON.md's headline (€107,205, 672 %, 3.3 months) didn't match the sheet | Updated from the rebuilt sheet |
| 3 #15 | HACKATHON.md's AI figures (53 calls, $7.90; $393.76) were stale | Updated from the rebuilt E15 and E18 |
| 3 #16 | "Rules only tighten" overstated: `protect-discount` keeps team deals even under the floor (`*` covers all), `margin-target` raises prices | Section 3 says what each rule kind does |
| 3 #17 | "Depth cap 25 %" without the person's lift to 90 % | Section 3 says it |
| 3 #18 | The demo's safety step: `cost-watch --inject … --test` writes to the test database, which the app never reads, so "the Chat tab shows the raise" couldn't happen | The demo script and the shot list show the decision line the command prints |
| 7 #40 | `pass.Applied` counted keeps (a pass that kept every row reported 56 applied) | Counts changes only; the test says so |
| 6 §3, 7 #36 | The person's campaign terms, goals and rule values are the model's reading of the message | Disclosed in section 3 (code checks ranges and records them under the person's name; it doesn't parse the message) |

## Left for the user (framing and facts only the user can settle)

- **The headline rate.** The juror's main objection is the same as 3b's and stronger: the restock-robust check the team
  built (E27, €2,749) and the controlled run (E20 low, €4,402) both sit far under €10,409, and E20's own method text says
  the year-over-year check "does not support the uncontrolled two". The headline is the user's framing call (Prompt 13);
  this run changed nothing in it, only disclosed every check beside it.
- **"Not claimed" vs the claim.** `DECLARATION.md:14-16` (the registration text, not edited) says the calculator's measured
  effect "is not claimed as hackathon benefit"; B0 is 71 % of the claim. The sheet's answer ("B claims no past euro, the
  next 12 months") is on record; the organisers rule.
- **"Without the build the test ends" vs E28.** October had four Kosovo runs and full pushes on four MK/AL layers on
  21 Oct. The line is labelled assumed; whether to keep it is the user's call.
- **The person's time.** The sheet counts 3.9 attended hours plus 1 assumed; the user made decisions throughout Saturday
  (commits `be566c1`, `def8b85`, `a9d135d`, Prompt 15, and the app sessions logged "0 (AI, at the user's request)"). Only
  the user knows those hours; at €50 an hour the effect is a few hundred euros.
- **`DECLARATION.md`'s wording** ("exceptions inbox", "go-live ready"): the registration text stays as registered.
- Still open from 3b and not fixable here: the database isn't in the repository (every runtime figure is unverifiable
  without it), the build transcripts aren't committed (E18), the kickoff rulings are recorded only by us, the score bands'
  source, the unsourced cost inputs (all labelled assumed), and the Për ty reproduction failing by one point (E11).
