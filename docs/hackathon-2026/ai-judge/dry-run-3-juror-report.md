# Juror report: Pricing Tool v2 (Track A, Money), third review, at `d980294`

**What I reviewed.** I looked at the repo at HEAD `d980294`: 172 commits, 170 of them after the labelled import `9c977ca`. I read DECLARATION.md, HACKATHON.md, the one-pager, numbers.json, backtest-results.md, hours.md, both dry-run-2 documents, the code and the git history. I also read the sheet dump (`../material/financial-sheet.txt`, cited as `fs`) and the team's rules summary (`../material/rules-summary.md`, cited as `rules`).

**Limits of this review:**
- **Score bands.** Only the net-benefit bands for ratings 4 and 5 come from `rules:16`. Every other net-benefit band, and all the ROI and payback bands, come from the team's own "Score bands" tab (`fs:646-694`). The tab is titled "from the hackathon rules", but I could not check that.
- **What I could not run or open.** The hackathon database, the session transcripts, the app and the tests. I also could not open the xlsx files: unzipping them needed an approval I didn't have.
- **What I could check.** I re-added the committed backtest snapshot by hand for levers 1, 3, 5 and 8 on both layers. It matches numbers.json.

---

## Scores

| Row (weight) | Team, B·Mid | **Mine, framing B** | Team, A·Mid | **Mine, framing A (cross-check)** |
|---|---|---|---|---|
| Net benefit, year 1 (40) | 5 (€107,326) | **3** (≈ €31k; range €12.9k–€50k) | 3 (€19,318) | **2** (≈ €12.9k) |
| ROI (10) | 5 (678 %) | **4** (≈ 199 %) | 4 (122 %) | **3** (≈ 81 %) |
| Payback (10) | 3 (3.3 mo) | **3** (≈ 4.9 mo) | 3 (5.8 mo) | **2** (≈ 6.7 mo) |
| Evidence (20) | 3 | **2** | 3 | **2** |
| Works (20) | 4 | **2** (3 if Run week is demoed live) | 4 | **2** (3) |
| **Points /100** | **84** | **54 (58)** | **66** | **42 (46)** |

**Framing A, corrected.** I made one change and kept every other input of the team's:

- **The change.** Lever 4 is valued at E20's controlled rate, €560, instead of €1,325. The higher figure borrows E20's uncontrolled before/after rate. The team's own check says that rate is not supported (numbers.json:484).
- **Result.** €3,393 a month, minus the 11 % holdout share, gives €3,020 a month.
- **Net benefit:** €3,020 × 9.5 − €15,838 = **€12,850** (band 2).
- **ROI:** 81 %.
- **Payback:** the position turns positive in month 7; interpolated, 6.7 months.
- **Sensitivity.** Lever 3's central figure is 93 % one August cost event (finding 25). If lever 3 is also scored without that event, net benefit falls to €2.9k (band 1).

**Framing B: does the claim hold? No.** The 5 on net benefit rests on three weak points:

1. **The rate is uncontrolled.** B·Mid uses B0 = €10,409, a before/after figure with no control. The team's own year-over-year check says it doesn't support that figure (numbers.json:484, `fs:863`). With the team's own controlled B0 (€4,402) and the corrected levers, B comes to €50.1k (band 4). On the team's B·Low row it is €38.3k (band 3). B·Mid clears the €100k line by only €7.3k: a 6 % cut in the monthly benefit (to €12,194, `fs:137`) drops it to band 4.
2. **The calculator already earns B0 today.** About 71 % of B·Mid's monthly benefit is B0 itself (€9,264 of €12,965), and lever 4 uses the same rate. The manual push process kept running through October: approvals #30, #32, #35 and #37 (`fs:908`; E23). So most of B0 is already earned by today's process.
   - The team's reason why v2 "delivers" B0 is that the manual push loses part of it, citing E23, E5 and E4 (HACKATHON.md:117-122).
   - But E5 and E4 are already counted as levers 5 and 4, and E23 as lever 8.
   - So the only incremental money the team names is already in the levers.
3. **It contradicts the registration.** The declaration says the calculator's effect is "not claimed" as hackathon benefit (DECLARATION.md:14-16).

**How I scored B.** I took the midpoint between two cases:
- B0 at the controlled rate and fully incremental: 4 / 5 / 3.
- B0 entirely baseline, so B equals corrected A: 2 / 3 / 2.

The midpoint (half the controlled B0 counted, levers corrected) is €31.5k net, 199 % ROI and 4.9 months payback, which scores 3 / 4 / 3. Which framing counts is the organisers' call. Even under framing B, the claimed 84 does not survive.

**Evidence = 2 for both framings.**
- **What's real:** the backtest code, the stored snapshot (which ties to numbers.json) and the E20/E21 SQL are committed.
- **Not rated 3:**
  - the elasticities are assumed;
  - B0's "controlled" low case rests on one run per layer, with controls chosen rather than random ("products it kept", "products not in it");
  - lever 2 is scored in-sample;
  - nothing has been measured live (the money ledger is empty, HACKATHON.md:211);
  - I could rerun nothing.

**Works = 2 on paper, 3 if demoed.**
- **Raise it to 3 if** the demo shows a full Run week on real data: Alpha → engine → check → sandbox publish → read-back.
- **It can't reach 4:**
  - the Për ty reproduction fails (E11);
  - the ledger is empty;
  - nothing has been published live;
  - with the default switch, 43 % of checked prices don't reach even the sandbox shelf (HACKATHON.md:232).

---

## Biggest unproven assumption

**That the calculator's Aug–Sep gain (B0) is both causal and lost without v2.**

- It makes up about 81 % of the headline: B0 directly, plus lever 4, which uses the same rate.
- The rate behind it (€10,409) is uncontrolled, and the team's own year-over-year check contradicts it (E25: margin ran +8.6 points over the year before with no calculator).
- The counterfactual "without v2 the gain disappears" is only stated (`rules:30`). The evidence points the other way: the manual pushes continued in October and also reached MK/AL (finding 12).

---

## Claims not verifiable

1. **High.** Every runtime figure needs the database, which is not supplied (HACKATHON.md:16-17). That covers E1–E17, E19 and E21–E26, the Run-week shelf counts (HACKATHON.md:164-167, :237-238), "hk.DataSources: 37 rows" (:252) and "all nine matched" on the Evidence page (:62). The snapshot JSON is the one exception, and its lever figures tie out.
2. **High.** E20's high case (€13,000) is quoted from the main repo with no script (`fs:351-352`). It drives B·High.
3. **Medium.** E20's low and mid cases have a committed script (`evidence/e20-test-phase.sql`), but I could not run it. The controls are chosen, not random: GjirafaMall's run vs "the products it kept"; Gjirafa50's run vs "products not in it", because it changed every product it held (`fs:340`).
4. **Medium.** E22, the cost watcher on its first real refresh (293 moves read within 0.7–9.1 minutes, 54 held; one-pager.md:19-21, HACKATHON.md:7-9), is checkable only through the database. "Ten checked by hand" is the team's word.
5. **Medium.** E18 ($272.70) comes from session transcripts that are not in the repo (HACKATHON.md:223). The logged model responses #19, #24 and #38 (HACKATHON.md:93-102) also can't be checked.
6. **Medium.** The kickoff rulings are recorded only by the team (`8544f43`, 16:52; kickoff-plan.md:115-122): reading the replica and the tool DB, and no API key.
7. **Low.** "967 passed, 1 skipped" and "692 at import, all still passing" (HACKATHON.md:52, :58, :261) could not be rerun.
8. **Low.** "Alpha's weekly pass #2 changed 8 rules" and "Nova planned Black Friday… held" (one-pager.md:31-33) depend on database records.

## Claims contradicted

9. **High. "B claims no past euro" vs the declaration.**
   - The sheet and the one-pager say B only uses the calculator's result "as the rate" (`fs:4`; one-pager.md:45-47).
   - B·Mid's monthly benefit is about 71 % that rate. DECLARATION.md:14-16 says the effect is "not claimed as hackathon benefit".
   - Rewording it as "the next 12 months" doesn't change whose money it is: the declared, non-AI calculator's (DECLARATION.md:9).
10. **High. B·Mid's headline uses the rate the team's own check rejects.** The E20 method says the year-over-year check "does not support the uncontrolled two" (numbers.json:484, `fs:863`). The headline (`fs:5`, HACKATHON.md:12-13) is built on one of those two.
11. **High. The reason given for B0 is money already counted.** HACKATHON.md:117-122 and one-pager.md:46-50 justify B0 with E23, E5 and E4. Those are already counted as levers 8, 5 and 4 (`fs:277-311`).
12. **Medium. "Only in Kosovo."** The one-pager says prices reach customers "only in Kosovo" (one-pager.md:8; also `rules:30`). The team's own lever-4 notes say MK/AL got manual pushes: Gjirafa50 AL in July, Mall AL in September, all four layers on 21 Oct (backtest-results.md:42-44). So lever 4's counterfactual (months with no push) doesn't describe today's process.
13. **Medium. "Going live is configuration and a DBA's grant."** That's the wording in one-pager.md:24-25 and HACKATHON.md:192-193. HACKATHON.md:230-231 itself also needs the `TierPrice` grant, the shop's answer on its price cache and the `OldPrice` convention, plus the commercial owner's decision on the supersede switch (PublisherOptions.cs:39-43).
14. **Medium. "An outcome tracker… rolls back what hurts"** is written in the present tense (one-pager.md:21-22). All applied changes are NotLive (hours.md:22) and the ledger has measured nothing (HACKATHON.md:211).
15. **Low. Stale figures in HACKATHON.md:**
    - E15 is given as "47 calls, $5.72" (:86); numbers.json says 53 calls, $7.90 (:382-383).
    - "24 unattended blocks" (:257); E16 says 36 (numbers.json:448), and hours.md has 37 unattended rows of 47 (lines 18-54), against E16's 36 of 46.
    - "129 commits" and "268 files / 57,015 insertions" (:31, :35) are dated "31 Oct 01:16". HEAD has 170 commits and 310 files / 74,237 insertions.
    - The file-count table (:42-48) gives Agent 115, NopIntegration 15, Tests 28. I count 124, 21 and 31 new files.
16. **Low. "A rule's block stays a block whatever the model says"** (HACKATHON.md:80). A blocked row with a model `fix_price` that passes the rule re-check becomes `Fixed`, and the model's price is exported (RunChecker.cs:131-138 → CheckedExport.cs:120, :126). The re-check bounds it (finding 33), so it's safe, but the wording is wrong.
17. **Low. "A person lifts only the step limit"** (FloorTargetAgent.cs:187-190; hours.md:19). `ApproveAsync` does not check the pause, so approving a paused hold (SafetyLimits.cs:70-72) also lifts the pause.

## Rule compliance

18. **Medium. Timing.**
    - Commit 1 (`b3f19a6`, 16:50) holds 1,237 lines prepared before the kickoff. That includes a 356-line Python sheet generator and `excel_recalc.ps1`. It is disclosed as tooling (HACKATHON.md:32-34).
    - The data scripts came within 30 minutes of the kickoff (`0410c08`, `87d225c`).
    - Commit sizes are plausible for AI-assisted work (e.g. `fcad1a4`: 1,500 lines in about 6 minutes). Committer dates prove nothing either way.
    - Every solution commit is dated after 16:30. The organisers rule on commit 1.
19. **High. Declared vs new.** The import is cleanly labelled (`9c977ca`). But the money claim is mostly the declared code's effect (findings 9-11).
20. **Pass. Secrets.**
    - I ran `git log --all -p -G` for `password=|pwd=|sk-ant|AccountKey=|PRIVATE KEY|User ID=|Uid=|Bearer|api_key=` over the whole history. It hits only documents that describe the check.
    - Tracked connection strings point at `(localdb)` only. `Copy-ToolDbKs.ps1` takes its source as a parameter.
21. **Low. Personal data.**
    - The sales export drops OrderId before its final select (sales_lines_ks.sql:57-64).
    - The tool-DB copy blanks emails and pseudonymises user names (Copy-ToolDbKs.ps1:12-39).
    - An employee's work email is still in history: `9c977ca`, and the removal diff in `80b8172`. It is disclosed.
    - The draft xlsx in commit 1 carries the Office user's name in its metadata (hours.md:36). I could not open it.
22. **Medium. Live systems.**
    - The team's own summary says "nothing connects to… the NOP replica during the build" (`rules:7`).
    - In practice the engine ran on the replica (hours.md:10). The cost watcher polls it every 15 minutes, and since 31 Oct a daily mirror step reads it too (HACKATHON.md:189-192).
    - This rests on the team-recorded ruling (`8544f43`, which rewrote CLAUDE.md's "Never connect to… the NOP replica").
    - A wrong launch profile wrote demo rows into the main repo's local dev DB. That is disclosed and left as is (HACKATHON.md:254).
23. **Medium. Every number labelled.** Mostly yes. Exceptions:
    - E21 (€14,705) is labelled "measured", but it is a counterfactual: lines lifted to the floor at the same units (`fs:871`).
    - The score bands are labelled "from the hackathon rules" but can't be traced (`fs:647`).
    - The cost and ramp inputs are "assumed" with no basis (finding 29).
24. **Pass. Mocks shown.** Sandbox-only publishing, the `[INJECTED-TEST]` test DB and a list of unfinished items are all present (HACKATHON.md:184-245). I could not see the UI banner.
    - **AI decides:** yes, structurally (findings 31-35).
    - **Money from AI decisions:** almost none (finding 36).

## Financial sheet

**Pass. The arithmetic checks out:**
- One-time cost: (3.9 + 1 + 160 + 8) × 50 + 272.7 = **€8,917.70** ✓ (`fs:463`)
- Running cost: 12 × (68.39 + 0 + 400) + 52 × 0.5 × 50 = **€6,920.66** ✓ (`fs:471`)
- Full-benefit months: (13 − 2 − 2) + 2 × 0.25 = 9.5 ✓
- B·Mid: (4,158 + 10,409) × 0.89 = 12,964.63; × 9.5 − 15,838.36 = **€107,325.63** ✓
- B·Mid payback: month 3 is −€4,165.5 and month 4 +€8,222.4, so 3.34 months ✓
- The lever totals tie to E8 (`fs:322`).

25. **High. Lever 3's central figure is one event.** In the snapshot, GjirafaMall's lever 3 at elasticity −1 is €2,554 in August and €184 in September (backtest-snapshot.json:647, :674). So 93 % of the €1,327 central assumes an August-sized cost event every other month. Only the low case (€148) drops it. Dry run 1's "one event × 12" problem is back in the central case.
26. **High. Framing B counts money today's process already earns** (findings 9-11).
    - Within the sheet I found no arithmetic double count between B0 and the levers: lever 1 is now scored in the calculator's era.
    - The double count is economic: B0, plus the very levers offered as the reason B0 is v2's.
27. **Medium. Lever 4.**
    - Its mid and high cases use E20's uncontrolled rates (`fs:282`).
    - Its "no push" counterfactual is contradicted by the October pushes (finding 12).
28. **Medium. Optimistic, unvalidated cases.**
    - Lever 8 high (€2,830) is October's anomaly, when the team stacked its own push sets, carried for 12 months (`fs:310`).
    - Lever 7's range is asymmetric (€703 / €783 / €1,744) on a planner with no leave-one-out calibration (E10) and a failing reproduction (E11).
    - Lever 2 is in-sample: rules set with October data, scored on Aug–Sep (backtest-results.md:58).
    - Lever 8 also keeps about €7 a month of team-deal lines (snapshot :812, `teamDealGain` 14), against its own rule. That last point is trivial.
29. **Medium. Inputs without a source:**
    - Hours: 1 h for the person's time in unattended blocks, 160 h to production, 8 h training, 8 h a month maintenance, 0.5 h a week review.
    - Servers €0. This assumes Claude Code headless on k8s with a subscription OAuth token, which is neither built nor costed (`fs:194`).
    - $1 = €1.
    - The pilot's start month, length and 25 % share (`fs:205-217`).
    - Every AI call volume (`fs:389-422`).
    - The elasticities.
30. **Medium. Scope and costs.**
    - The sheet takes full six-layer scope from month 4. The pilot ask is a few Kosovo categories (one-pager.md:82-85), and the MK/AL cycle doesn't run.
    - **Hours.** Attended time is 3.9 h plus 1 h assumed. But the person directed work across roughly 12 hours of clock time: "the user's goal… in chat" (hours.md:25), "the user's decision (relayed by the other session)" (hours.md:54). The money effect is small, about €400.
    - **Not costed:** pilot measurement, the commercial owner's supersede decision, the DBA grant, and the shop's cache answer.
    - **Pass:** revenue as margin (none; all gains are gross profit at booked COGS) and hours saved as money (not counted, `fs:141`).

## AI decides / code enforces

31. **Pass. Alpha → floors and targets.**
    - The model's JSON goes through `SafetyLimits.Evaluate` (FloorTargetAgent.cs:149-154). Its checks (SafetyLimits.cs:42-81): holdout and thin rows kept; out-of-range values, target below floor and floors under the hard minimum rejected; paused rows held; more than 1 point a week held.
    - Only Applied non-keeps are written (FloorTargetAgent.cs:167).
    - A missing row is rejected with nothing changed (:140-146).
    - Approval re-checks the hard minimum, target ≥ floor, holdout and staleness (:204-221).
32. **Pass. Campaign prices.**
    - Every price is computed by code (CampaignPlanner.cs:100-116).
    - Each SKU is now bounded at cost / (1 − 3 %), and a price under it is discarded (:105, :111; test CampaignPlanningTests.cs:158).
    - The minimum margin is raised to the floor in both rounds (CampaignPlanning.cs:112-118, :198-202), and the depth is capped (:134, :203-205).
    - A person's goal can't be changed (:206-211), and a plan short of it is held (:161-167).
    - **Low gaps:** `MinMarginFloorPct` is configuration with no lower bound, and a value of 0 disables the per-SKU floor (CampaignLimits.cs:27; CampaignPlanner.cs:105 `> 0`). `CampaignLimits` still accepts a brief at 0 % (CampaignLimits.cs:61).
33. **Pass. Run checker.**
    - This is the one place a model number becomes a price directly: `fix_price`.
    - The fix is accepted only if every rule re-checks at Info or below (RunChecker.cs:131-138). That means price > 0, not below cost, not below the floor, not above max(anchor, current), at most 20 % change, and on the charm grid (RunCheckRules.cs:65-102).
    - Warn and Block rows are held; a blocked run exports and publishes nothing (CheckedExport.cs:111-119; CheckedPricePublisher.cs:70-71).
    - Rows over the 150-row cap keep their rule severity (RunChecker.cs:84-89).
34. **Pass. Publisher.**
    - With `Target=Sandbox`, any schema outside `shop_sandbox_*` throws (PublisherOptions.cs:56-61), and every config says Sandbox.
    - A raise above the run's ceiling, or without one, is refused again in the writer (NopRegularPriceWriter.cs:43-46).
    - **Low:** the guard is on the schema name, not the server (disclosed).
35. **Medium. A chat goal is the model's number, stored as the person's.** `GoalValue = b.GoalValue` is Nova's transcription, while `GoalSetBy` names the person (AgentChat.cs:228, :261). This is disclosed (HACKATHON.md:228-229).
36. **Medium. The euros barely depend on the AI's decisions.**
    - Alpha's floors are worth €2 a month (E2).
    - Lever 7 is code's per-SKU depth; the model only picks among four strategies code computed.
    - The run checker sees at most 150 rows.
    - Under framing B, more than 90 % of the claim is the non-AI calculator and automation.
    - The AI is real and fenced in, but it isn't what earns the money.

## Other findings

37. **Low.** The latest features landed minutes before the record: the mirror step at 09:44, the Evidence page at 10:07, and the switch to framing B at 10:12.
38. **Low.** The 57 % and 98.3 % shelf shares are measured on different runs: 172,579 vs 174,087 checked prices (HACKATHON.md:164-165, :237-238).
39. **Low.** `pass.Applied` still counts keeps (FloorTargetAgent.cs:169). The UI shows "changed".

---

## Dry run 2 findings

| # | Sev. | Status now | Evidence |
|---|---|---|---|
| 1 | high | **Partly** | `backtest-snapshot.json` is committed and its levers 1/3/5/8 tie to numbers.json (I re-added them). Most runtime figures still need the DB. HACKATHON.md:86 is stale against E15 |
| 2 | high | **Partly** | `e20-test-phase.sql` and `e21-by-cause.sql` are committed. The high case is still quoted (`fs:351`). I couldn't run them |
| 3 | med | **Partly** | The wording changed and E22 has a SQL script. The rollback side is unmeasured, and one-pager.md:21-22 still says "rolls back what hurts" |
| 4 | med | **Disclosed only** | HACKATHON.md:225-227 |
| 5 | med | **Disclosed only** | HACKATHON.md:223; only aggregates in `ai-usage.json` |
| 6 | low | **Not resolved** (inherent) | Tests can't be run here |
| 7 | low | **Resolved** (wording) | "Floor" dropped and a Gjirafa50 controlled run added (`fs:337-340`). Its control is weak (finding 3) |
| 8 | high | **Partly** | Reworded to "daily checked prices, which the team still pushes" (HACKATHON.md:161). The cycle publishes to the sandbox (CheckedPricePublisher.cs:59-75). Live is still the push file |
| 9 | high | **Resolved** in code | CampaignPlanner.cs:105, :111; test :158. Remaining: the floor can be configured to 0 (finding 32) |
| 10 | med | **Disclosed only** | AgentChat.cs:228, :261 unchanged; HACKATHON.md:228-229 |
| 11 | med | **Partly** | E16's label now reads "attended rows only" (numbers.json:447). The substance stands: hours.md:25, :54 |
| 12 | med | **Not resolved, now worse** | The rejected uncontrolled rate is now the headline (finding 10) |
| 13 | low | **Not resolved** | The figures are dated 01:16, not current (finding 15). The team's "fixed: current figures" is not true at HEAD |
| 14 | low | **Resolved** | All 20 modified files plus the new ones are named (HACKATHON.md:47). Only the migration snapshot is unnamed |
| 15 | low | **Resolved** | HACKATHON.md:83 cites `PublisherOptions.cs:58`; no `PublisherOptions.Validate` reference remains |
| 16 | low | **Not resolved** (kept) | FloorTargetAgent.cs:169 |
| 17 | med | **Disclosed only** | Unchanged |
| 18 | med | **Partly** | E16's label and hours.md:37's CSV note are fixed. E21 is "measured" and the costs are unsourced (findings 23, 29) |
| 19 | med | **Disclosed only, and wider** | Replica reads now also come from the cost watcher and the daily mirror (finding 22) |
| 20 | pass | **Pass** | My history scan (finding 20) |
| 21 | pass | **Pass, low note unchanged** | Email still in `9c977ca` and `80b8172`; xlsx metadata in commit 1 |
| 22 | pass | **Pass** | HACKATHON.md:184-245 |
| 23 | med | **Not resolved** (disclosed) | With framing B as the claim, the AI's share of the euros fell further (finding 36) |
| 24 | pass | **Pass** | Re-verified above |
| 25 | med | **Not resolved** (kept) | Finding 29 |
| 26 | high | **Resolved, new problem** | Every lever is now on Aug–Sep (snapshot months tie). Lever 3's central figure is again one August event (finding 25) |
| 27 | high | **Partly** | The lever-1 overlap is gone. B0 is still the existing process's money, justified by levers already counted (findings 11, 26) |
| 28 | med | **Resolved** in the sheet | Holdout share 0.11 (`fs:324-329`, F4 formulas); recomputed 23,633 / 220,472 = 10.7 % ✓. The backtest itself still doesn't exclude it (disclosed) |
| 29 | pass | **Pass** | — |
| 30 | pass | **Pass** | `fs:141` |
| 31 | med | **Not resolved** (kept) | `fs:299-303` |
| 32 | med | **Partly** | The regular-price writer is built on the sandbox. Pilot measurement, the owner's decision, the DBA grant, the shop cache and the other session's hours are not costed |
| 33 | pass | **Pass** | Small wording issue (finding 17) |
| 34 | pass | **Pass, improved** | Finding 32; the chat goal remains (finding 35) |
| 35 | pass | **Pass** | Wording issue (finding 16) |
| 36 | low | **Disclosed only** | HACKATHON.md:243; PublisherOptions.cs:61 |
| 37 | med | **Not resolved** (kept) | Finding 30 |
| 38 | low | **Resolved** | Now in HACKATHON.md:228-229 |
| 39 | low | **Partly** | Run weeks on both Kosovo layers are recorded (hours.md:43). New features again landed just before the record (finding 37) |
| Biggest assumption (raises can't reach the shelf) | high | **Partly** | `NopRegularPriceWriter` writes raises as the regular price, with ceiling and changed-since-run guards (:43-52). 57 % of checked prices reached the **sandbox** shelf; 98.3 % only with a switch that stays off. Never run live; going live needs the grant, the cache answer and the owner's decision |

**Summary.** Of the 39 findings and the biggest assumption:

- **Resolved:** 7 (#7, #9, #14, #15, #26 with a new problem, #28, #38).
- **Partly:** 11.
- **Disclosed only:** 7.
- **Not resolved:** 8.
- **Pass:** 7.

The fixes since dry run 2 are real: per-SKU campaign floors, regular-price raises on the sandbox, the scripts committed, the calculator-era window and the holdout share. But two moves pushed the headline score up, not the evidence:

- the switch to framing B as the claim;
- B·Mid built on a rate the team's own check rejects.

I could not run the app, the tests, the database scripts or any Python/PowerShell, and I could not open the xlsx. I didn't write the report to a file or change anything in the repo.