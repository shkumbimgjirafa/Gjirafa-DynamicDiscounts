# AI-judge dry run 2 (31 Oct 2026, 01:18–01:26)

The same juror as [dry run 1](dry-run-1.md), on the state after its fixes, Run week and the code review: `e47f481`.
What it found, and what we did. Dry run 3 on the final state stays planned for Mon 06:00.

## How it ran

The same isolation and prompt as dry run 1: Claude Code 2.1.314 headless, `claude-opus-5-5`, `--setting-sources ""`,
`--no-session-persistence`, no `CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables, Read / Grep / Glob and `git log / show /
diff / grep` only. It read a fresh clone (no remote), the rules summary and a fresh cell-by-cell dump of the financial
sheet. One item was added to the prompt: for each finding of dry run 1's report, say whether it is resolved now, without
taking our answer on trust. 57 turns, 485 s, $3.84 at API list price. Its full report:
[`dry-run-2-juror-report.md`](dry-run-2-juror-report.md).

## Scores

| Row (weight) | Juror, dry run 1 (A / B) | Juror, dry run 2 (A, our numbers / A, its corrections / B) | Our sheet after the fixes below (A · Mid / B · Mid) |
|---|---|---|---|
| Net benefit (40) | 2 / 4 | 3 / 2 / 4 | 3 / 5 |
| ROI (10) | 4 / 5 | 4 / 3 / 5 | 4 / 5 |
| Payback (10) | 3 / 4 | 3 / 2 / 3 | 3 / 3 |
| Evidence (20) | 2 / 1 | 2 / 2 / 1 | 3 / 3 (our judgment) |
| Works (20) | 2 (3 if demoed) | 2 (3 if Run week is demoed) | 4 / 4 (our judgment) |
| **Total** | **46 (50) / 62 (66)** | **54 (58) / 42 (46) / 60 (64)** | **66 / 84** (A low 40, A high 68; B low 68, B high 84) |

It still picks framing A, for the same reason (framing B's extra money is the declared, non-AI calculator's effect).

## Dry run 1's findings, as it judged them

3 resolved outright (#13 the E20 label, #15 the Evidence reasoning, #36 the run checker's blocks), 3 resolved in
wording or structure (#5, #14, #37), 7 pass unchanged, 17 partly, 9 disclosed only, 2 not resolved (#3 the "floor"
wording of B0's low case; #28 framing B's use of the calculator's money). One of our fixes **introduced a new error**
(#27, below as its finding 26). The table with its evidence is in its report.

## Its new findings, and what we did

Fixed = changed in code, data or documents; disclosed = stated in `HACKATHON.md` (section 5, "Known limitations", or
where the claim is); kept = left as it is, with the reason.

| # | Severity | Finding | Outcome |
|---|---|---|---|
| 26 | high | The 12-month window for levers 1 and 3 (our fix to dry run 1's #27) scored them against months before the calculator, a baseline framing A doesn't have: lever 1 rose €196 → €880 on those months | **Fixed:** every lever back on the calculator's era, Aug–Sep 2026; lever 3's low case leaves out August's one cost event instead (E3 €1,532 central, €327 low). E8 €4,266 / €1,999 / €5,791 |
| 27 | high | Framing B added the calculator's gain (B0) to a lever 1 measured mostly before the calculator | **Fixed** by #26: lever 1 is now measured in the calculator's era, after its effect. The shared 5.9 % rate behind B0 and E4 stays **disclosed** |
| 9 | high | "Never under a 3 % margin floor" read as per SKU, but the 3 % was the plan's blended minimum; single SKUs could be planned at 0–3 % and the cost watcher would then treat them as breaches | **Fixed in code:** the planner bounds every SKU's campaign price at the 3 % floor (`PlanSpec.SkuMarginFloorPct`); test |
| 8 | high | "Daily publishing" (lever 5) counted as automation the agents run, but the daily cycle writes a push file, not a publish | **Fixed wording:** daily checked prices, which the team still pushes |
| Biggest assumption | high | The money levers are mostly raises, and the publisher can only lower a price (a discount); 68 % of Run week #1's prices didn't publish (cheaper team deals, raises) | **Disclosed:** the one-pager's biggest unproven assumption and `HACKATHON.md`; writing regular prices back is part of the remaining build. *After Prompt 07:* **built and tested on the sandbox** (`NopRegularPriceWriter`): raises are written as the store's regular price, with audit, rollback, the ceiling and a changed-since-the-run guard; Run weeks on both KS layers put 99,197 of 172,579 checked prices on the sandbox shelf (57 %; 909 raises, and 258 more written under a cheaper team deal). Still **not live**, and cheaper team deals still hold 43 % off until a person supersedes them |
| 28 | medium | The backtest doesn't leave out the holdout categories, which the agents never change | **Fixed:** the sheet leaves out their share (11 %, estimated from the 31 Oct exports' changed prices) |
| 3 | medium | "A cost watcher reacts within 15 minutes" and "rolls back what hurt" are unproven (no real cost move; nothing live) | **Fixed wording:** "checks costs every 15 minutes"; "rolls back what hurts", with the ledger and tracker listed as not measured yet. *After Prompt 09:* the first real cost refresh (31 Oct morning) is measured: 293 cost moves read within 1–9 minutes of the refresh, none raised (no rise put a price we set under its floor), 54 held for a person (52 at a cheaper team deal, 2 with a floor above the shelf); ten checked by hand; a duplicate observation across two polls fixed (E22). Still nothing live, and the rollback side is unmeasured |
| 10, 38 | medium / low | A chat goal is binding as the person's, but the number is Nova's reading of the message; disclosed only in a side document | **Disclosed** in `HACKATHON.md` |
| 11, 18 | medium | E16 labelled "measured" although the person worked inside "unattended" blocks; hours.md said the backtest CSV was committed (it isn't) | **Fixed:** E16's label now says attended rows only; the hours row says the CSV wasn't committed. **Kept:** the 1 h assumed for the person's time; the "other session's requests" in the log are another Claude session's, not the person's |
| 13, 14 | low | Stale history figures; changed imported files under-listed (16 named, 25 changed) | **Fixed:** current figures, all 25 named |
| 15 | low | `PublisherOptions.Validate` cited, doesn't exist | **Fixed:** `ShopFor` |
| 36 | low | With Target = Sandbox the guard is on the schema name, not the server | **Disclosed** |
| — | low | The imported README links to three main-repo files this repo doesn't copy (found by us) | **Fixed:** the README says so |
| 2 | high | E20 and E21 were measured in the main repo, with no script here | *After Prompt 07 (31 Oct morning):* **fixed** for E20's low and mid cases and E21: recomputed from this repo's data (`evidence/e20-test-phase.sql`, run by `build-numbers.ps1`; `backtest-below-floor`). E20 low €4,402 (one controlled run per KS layer), mid €10,409; the high case's 2025 adjustment stays quoted from the main repo, **disclosed** |
| 1, 4, 5, 19, 23 | high / medium | The database, the transcripts and the kickoff rulings aren't in the repo; the AI's own decisions carry little of the euro | **Disclosed** (unchanged). *After Prompt 07:* the stored backtest run is committed as `evidence/backtest-snapshot.json` |
| 6 | low | 917 tests can't be rerun without running them | **Kept:** `dotnet test PricingTool.sln` runs from a clean clone (918 now) |
| 7, 12 | low / medium | B0's low case called "a floor"; the team's own year-over-year check doesn't support the uncontrolled cases that feed E4's mid and high | **Kept, disclosed:** "a floor" because one run on one of two layers; E4's controlled-only value (€509) is stated next to it. *After Prompt 07:* **fixed** for 7: a controlled run on Gjirafa50 too (#1 vs products not in it), so the low case is one controlled run per layer (€4,402) and "a floor" is dropped. 12 stays disclosed (E4 controlled-only now €560) |
| 16 | low | The stored `pass.Applied` counts keeps | **Kept:** the field means applied decisions, keeps included; the page and the one-pager say "changed" |
| 17 | medium | Data tooling committed 30 minutes after the kickoff looks prepared | **Kept:** the commit dates are what they are; the organisers can rule, as for commit 1 |
| 25, 31, 37 | medium | Assumed costs and ramp without a source; E7's range asymmetric; the sheet's full scope from month 4 includes MK/AL, whose cycle isn't loaded yet | **Kept:** each is labelled assumed with its reason; E7's range is the stored curve sensitivity; MK/AL loads are in the remaining build |
| 39 | low | The demo rests on Run week, built minutes earlier, with one recorded run | **Kept:** true; the demo presses it before step 2 |

After these fixes our sheet says: framing A €1,779 / €3,797 / €5,154 a month after the holdout share, year-1 net
benefit €1.1k / €20.3k / €33.2k, score 40 / 66 / 68; framing B €34.9k / €110.2k / €143.1k, score 68 / 84 / 84.
