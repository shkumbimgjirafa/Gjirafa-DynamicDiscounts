# AI-judge dry run 1 (31 Oct 2026, 00:19–00:28)

Plan section 10: a fresh Claude, with no context from the build, reads the submission as a skeptical juror. What it
found, what we changed, and what we left as it is, with the reason.

## How it ran

- **Model and isolation.** Claude Code 2.1.314 headless (`claude -p`), `claude-opus-5-5`, `--setting-sources ""` (no
  user or project settings, no memory), `--no-session-persistence`, the parent's `CLAUDE*` / `ANTHROPIC_*` / `MCP_*`
  variables removed. Tools: Read, Grep, Glob and `git log / show / diff / grep` only; no edits, no web.
- **What it read.** A fresh clone of this repo at `eb6572c` (no remote), plus two files outside it: the rules summary
  from `kickoff-plan.md` section 1 (the official rules document and jury sheet aren't in the repo) and a cell-by-cell
  dump of the financial sheet with each formula and Excel's cached value (it can't open .xlsx).
- **Cost.** 59 turns, 504 s, $3.62 at API list price (`total_cost_usd`; the subscription pays). A build-time check, so it
  is counted in the build's own AI usage (E18) as an extra: the call left no transcript.
- **Not run yet:** `/code-review` and `/security-review` on `9c977ca..HEAD` (about 52k lines). Planned for the second
  dry run (Mon 06:00).

The prompt, word for word:

> You are a skeptical juror for the Gjirafa AI Hackathon 2026, Track A (Money). The repository in the current folder is
> one team's submission. [Material: the rules summary, DECLARATION.md, HACKATHON.md, the one-pager, numbers.json, the
> financial sheet and its dump, the code and the git history.] Do this: 1. Score each Track A row 0-5 [...] If which
> framing counts is ambiguous, score both and say which you would pick. 2. Name the single biggest unproven assumption.
> 3. List every claim [...] that you cannot verify from this material, and every claim that the code, the git history or
> another document contradicts. Cite file:line. 4. Check the rules [...]. 5. Check the financial sheet's arithmetic and
> formulas against the Track A formula. Flag double counting, revenue counted as margin, hours saved counted as money,
> and inputs without a source. 6. Spot-check "the AI decides, code enforces" [...]. Be concrete and skeptical; give no
> credit for anything you could not check.

Its full report: [`dry-run-1-juror-report.md`](dry-run-1-juror-report.md).

## Its scores

| Row (weight) | Ours before (A · Mid / B · Mid) | Juror, framing A | Juror, framing B |
|---|---|---|---|
| Net benefit (40) | 3 / 5 | 2 | 4 |
| ROI (10) | 5 / 5 | 4 | 5 |
| Payback (10) | 4 / 4 | 3 | 4 |
| Evidence (20) | 4 / 4 | 2 | 1 |
| Works (20) | 4 / 4 | 2 (3 if the demo shows the daily cycle) | 2 (3) |
| **Total** | **74 / 90** | **46 (50)** | **62 (66)** |

It would pick framing A. After the fixes below, our own sheet says A 54 / 66 / 68 and B 76 / 84 / 84 (low / mid / high),
with Evidence 3 and Works 4 as our judgment.

## What it found and what we did

Numbers are the report's. Fixed = changed in code, data or documents; disclosed = now stated in `HACKATHON.md` (section 5,
"Known limitations", or where the claim was); kept = left as it is, with the reason.

| # | Severity | Finding | Outcome |
|---|---|---|---|
| 35 | med-high | Nova sets a campaign's minimum margin with no floor in code; a review round could lower it to 0 | **Fixed** (`56078a4`): a 3 % floor in code on the brief and every review adjustment, stored on the brief (the cost watcher uses it); test |
| 36 | medium | The model could downgrade any rule block to a warn, unblocking the run | **Fixed** (`56078a4`): a rule's block stays a block; the prompt says so; test |
| 11 | high | The pilot covers a few categories, the sheet counted full scope from month 2 | **Fixed**: the sheet ramps (a two-month pilot at an assumed 25 %, then full scope: 9.5 full-benefit months) |
| 27 | high | A two-month window (one August cost event) multiplied by 11 | **Fixed for levers 1 and 3**: averaged over 12 months (lever 3 €770 instead of €1,527; lever 1 €880 instead of €196). Levers 2, 5, 6 need the Aug–Sep floors and approvals: two months, **disclosed** |
| 31, 39 | medium | Lever 7 had no range; A · High cleared €40k only through E4's high case | **Fixed**: E7 low / high from the uplift curve at half and twice its slope (€703 / €1,744); A · High is now €37.7k (band 3) |
| 14, 15 | medium | Works tied to the money cases; the Evidence 4 reasoning ("every input has a script") was not true | **Fixed**: one Works rating (4) and Evidence 3 in both framings, each with its reason |
| 12, 25, 32 | medium | The person's time inside unattended blocks; 4 h a month of maintenance; no code review or sign-off of the limits | **Fixed**: 1 h assumed for the person's chat time, 8 maintenance hours a month, 160 h to production incl. code review, 8 h training and sign-off |
| 13, 23 | medium | E20's season-adjusted case labelled measured; elasticities hidden under "estimated" | **Fixed**: E20 high is "estimated: before/after season-adjusted, not controlled"; every lever's method says the elasticity is assumed |
| 9 | high | Lever 4 uses E20's uncontrolled rates, while we said framing B is never added into framing A | **Disclosed**: the dependency is stated and E4's controlled-only value (€509) given. Kept as the plan defines lever 4 |
| 33 | high | Almost none of framing A's money comes from the model's own decisions (lever 2: €4) | **Disclosed**: "Where the money comes from" in `HACKATHON.md` and the one-pager |
| 10, 28 | high | Framing B counts the non-AI calculator's effect, which the declaration did not claim | **Disclosed**: the organisers' call, stated in the sheet, the one-pager and `HACKATHON.md`. Kept: "the same 5.9 % twice" is two different bases (Kosovo for B0, MK/AL for lever 4), not one euro counted twice |
| 1 | high | The database isn't supplied, so runtime figures can't be checked | **Disclosed and eased**: `HACKATHON.md` says what needs the database; the levers' monthly figures are in `backtest-results.md` and the Backtest page's CSV (`*.csv` stays out of git by rule) |
| 2, 7, 21 | high / med | E20, E21 not reproducible here; the kickoff rulings recorded only by us | **Disclosed**; written confirmation of the rulings is on the user's list |
| 19 | medium | Commit 1 carries the plan and the financial-sheet tooling prepared before the kickoff | **Disclosed** in section 1 |
| 22 | low | An employee's work email as the imported default admin; an Office name in the draft xlsx | **Fixed**: removed from the tree (it stays in the import commit); the xlsx metadata is cleared on every recalculation |
| 6, 37 | medium / low | "Go-live is configuration" though never run against a shop; the sandbox guard holds only while Target = Sandbox | **Fixed** wording |
| 16 | medium | Servers €0 "on k8s" while the live path is a laptop | **Fixed** note: production runs on the cluster that runs the calculator today; the laptop is the hackathon's stand-in |
| 5 | low | "Alpha's rules raise floors 1–3 points" vs a 1-point step limit | **Fixed** wording: each rule moved one point; they sit 1–3 points above the lower hand-set floors of Aug–Sep (checked: all 8 applied changes are +1) |
| 17, 18, 8 | low | "Reused unchanged" engine; stale figures; 900 vs 909 tests | **Fixed** |
| 29 | low-med | Lever 1 assumes a blocked loss sale costs nothing else | **Disclosed** |
| 38 | medium | 8,000 requests a month vs about 290 calls in the cost line | **Kept**: the volume is what the system needs; no call is made to raise a count. Disclosed |
| 3 | medium | €4,000 is "near the top" of €3.5–4.3k | **Kept**: it is about the middle; "a floor" because it is one run on one of the two layers |
| 4 | medium | "511k SKUs" vs run #1099's 516,122 | **Kept**: run #1101 priced 511,093; the one-pager now names the runs |
| 20, 24, 26, 30, 34, 40 | pass / low | Import labelled, mocks shown, arithmetic right, hours saved not counted, Alpha's limits in code, approve-first mode disclosed | No change |

## Found by us during the same block (not by the juror)

A web start with the "http" launch profile ran in the Development environment, which falls back to `appsettings.json`:
at 00:20:58 it migrated and seeded the main repo's local development database `PricingTool` on this laptop. No shop or
replica database was touched (demo-data mode; no `.env` exists). The web app and the engine now refuse to start in any
environment but Hackathon (`f9dfb1f`).

**The schema is reverted; the data is not.** At 00:25, after a copy-only backup
(`%LOCALAPPDATA%\PricingToolAgent\backups\PricingTool-devdb-before-revert-20261031.bak`), every object created at
00:20:58–59 was dropped: the tables `BandCategoryRules` and `PricingRunCategoryRules` with their two migration-history
rows, the `ai` schema (28 tables), the `agent` schema and the `agent_reader` user. The history ends again at the main
repo's `20261021073844_AddPricingRunBandTargets`. (`f9dfb1f`'s message says "the same minute"; it was five minutes.)

What the start's demo seeding wrote and is still there (everything dated 30 Oct or later; the database's previous
activity was 29 Oct):

| Table | Rows | Inserted or changed |
|---|---|---|
| `PricingRuns` | 1 (#40010, "demo-seed", 23:21:05–23:21:11 UTC) | inserted |
| `ProposedPrices` | 360 (run #40010) | inserted |
| `PricingRunBandTargets` | 8 (run #40010) | inserted |
| `DailySnapshots` | 360 (30 Oct) | inserted |
| `DeadStockEpisodes` | 40 (entered 30 Oct) | inserted |
| `DailyLayerKpi`, `DeadStockDailyKpi` | 1 + 1 (30 Oct) | inserted |
| `AuditLog` | 2 (run started / finished) | inserted |
| `SkuDeadStockStates` | 360 (snapshot 29 Oct → 30 Oct) | **changed** |
| `Layers` | 1 (`LastSlowLaneRunUtc` set to 23:21:11) | **changed** |

The backup was taken after the start, so it holds the changed rows' new values, not their old ones; the dead-stock
states keep only part of their previous values (`Prev*` columns). Deleting only the inserted rows would leave the 360
states and the layer pointing at a day with no run or snapshots. So we left the data as the start wrote it (one more
day of the imported calculator's own Development demo seeding, which the main repo's app writes on its next
Development start anyway) and put the decision on the user's list. If the user wants the inserted rows gone and has no
older backup, this removes exactly them (it doesn't restore the 361 changed rows):

```sql
BEGIN TRAN;
DELETE FROM PricingTool.ProposedPrices        WHERE PricingRunId = 40010;
DELETE FROM PricingTool.PricingRunBandTargets WHERE PricingRunId = 40010;
DELETE FROM PricingTool.PricingRuns           WHERE Id = 40010;
DELETE FROM PricingTool.DailySnapshots        WHERE SnapshotDate = '2026-10-30';
DELETE FROM PricingTool.DeadStockEpisodes     WHERE EnteredDate >= '2026-10-30';
DELETE FROM PricingTool.DailyLayerKpi         WHERE [Date] >= '2026-10-30';
DELETE FROM PricingTool.DeadStockDailyKpi     WHERE [Date] >= '2026-10-30';
DELETE FROM PricingTool.AuditLog              WHERE TimestampUtc >= '2026-10-30T23:15';
-- expect 360, 8, 1, 360, 40, 1, 1, 2 rows; then COMMIT (or ROLLBACK if any count differs)
```
