# Juror report: Pricing Tool v2 (Track A, Money), second review

**What I reviewed.** I read the repo at `e47f481`: 131 commits, 129 of them after the labelled import `9c977ca`. I used DECLARATION.md, HACKATHON.md, the one-pager, numbers.json, backtest-results.md, hours.md, the code, the git history, the sheet dump (`../material/financial-sheet.txt`, cited as `fs`) and the team's rules summary (`../material/rules-summary.md`, cited as `rules`).

- **Bands.** Only the net-benefit bands for ratings 4 and 5 come from `rules:16`. All other net-benefit, ROI and payback bands come from the team's own "Score bands" tab (`fs:632-661`). I could not check them against the official rules.
- **What I could not run.** The hackathon database, the session transcripts and any backtest output are not in the repo. So no runtime figure, test or backtest was rerun, and none gets credit.

---

## Scores

| Row (weight) | Team (A·Mid / B·Mid) | **Framing A, team's numbers** | **Framing A, my corrections** | Framing B, my score |
|---|---|---|---|---|
| Net benefit, year 1 (40) | 3 / 5 | 3 (€23,955) | **2** (≈ €9.4k) | 4 (B·Low only) |
| ROI (10) | 4 / 5 | 4 (151 %) | **3** (≈ 60 %) | 5 |
| Payback (10) | 3 / 3 | 3 (5.4 mo) | **2** (≈ 7.5 mo) | 3 (≈ 4.2 mo) |
| Evidence (20) | 3 / 3 | 2 | **2** | 1 |
| Works (20) | 4 / 4 | 2 (3 if demoed) | **2** (3 if demoed) | 2 (3) |
| **Points /100** | 66 / 84 | 54 (58) | **42 (46)** | 60 (64) |

**I would pick framing A.** Framing B's extra money (B0) is the effect of the declared, non-AI calculator (DECLARATION.md:9). The declaration says that effect is "not claimed" (DECLARATION.md:14-16). The calculator already runs today with manual pushes (DECLARATION.md:7-13), so B0 is already in today's baseline.

**How I corrected framing A** (all other inputs are the team's):

- **Lever 1 back to the calculator era (€880 → €196).** The 12-month window inflates lever 1 4.5×, because it takes in the months before the calculator started (finding 1).
- **Lever 4 at its controlled rate only (€1,353 → €509).** The higher figures borrow E20's uncontrolled rates.

That gives **€2,658 a month**.

- Net benefit: €2,658 × 9.5 − €15,812 = **€9,439**, band 2.
- ROI: 60 %, band 3.
- Payback: on the sheet's own monthly grid the position turns positive in month 8; interpolated, **7.5 months**, band 2.
- This is before the raise problem in finding 2, which could cut levers 1, 3 and 5 further. I can't size that cut from the repo.

**Evidence = 2.**
- The backtest code exists and is coherent (`LeverBacktest.cs`).
- But no output is in the repo. hours.md:37 says the stored backtest run was "committed as CSV", yet `git log --all -- "*.csv"` returns nothing.
- The elasticities are assumed.
- Nothing has been measured live (HACKATHON.md:159).

**Evidence for B = 1.** B0 has "script: not in this repo" (numbers.json:306-307).

**Works = 2 on paper.** I could not run anything. Raise it to 3 if the demo shows Run week end to end (Alpha → engine → check → sandbox publish → read-back). It can't reach 4 here:
- regular prices are never published (HACKATHON.md:155-158);
- the Për ty reproduction fails (E11);
- the money ledger has measured nothing.

---

## Biggest unproven assumption

**That what v2 decides will actually reach the shelf, in particular the price raises that framing A's main levers need.**

- **Half of framing A depends on raises.** Levers 1, 3 and 5 are €2,046 of the €4,186 (49 %). Each makes money by raising a price that is too low: blocking a loss sale, reacting to a cost rise, publishing the engine's price sooner.
- **The publisher can only lower a price.** It writes discounts only, and "a discount can only lower a price" (NopDiscountWriter.cs:22, :243). Raises above the shelf price go to the manual push file (HACKATHON.md:157-158; RunWeekLoop.cs:144).
- **Cheaper team discounts win.** The publisher leaves them alone (`SupersedeTeamRegularDiscounts` is false by default; PublisherOptions.cs:39-42), and CostWatcher holds those SKUs for a person (CostWatcher.cs:317-322).
- **The one real Run week mostly could not publish.** Run week #1 created 11,917 prices and left 25,811 to cheaper team deals, plus 198 raises (hours.md:40). That is about 68 % of the run's prices.
- **The backtest ignores all of this.** It never models team discounts, the shelf price for unpublished regular prices, or the manual push.
- **Nothing has been live.** Every gain is a simulation of prices that never went live.

---

## Claims not verifiable

1. **High.** Every runtime figure (E1–E15, E17, E19; "47 calls, $5.72" at HACKATHON.md:74; "hk.DataSources: 32 rows" at HACKATHON.md:177) needs the database, which is not supplied (HACKATHON.md:9-12). The backtest CSV the team cites is also absent (finding 18).
2. **High.** E20 and E21 were measured in the main repo, with no script here (numbers.json:306-307, 319-320). They carry all of framing B and lever 4's mid and high cases (`fs:281`).
3. **Medium.** The calls behind "a cost watcher reacts within 15 minutes" (one-pager.md:15) and "rolls back what hurt" (one-pager.md:15-16) are unproven.
   - CostWatcher has never raised a real price: "0 moved since today's refresh" (hours.md:24).
   - The outcome tracker shows all 8 changes as NotLive (hours.md:22).
   - The 15 minutes depends on how fast the replica's cost refreshes, which the team itself calls "a go-live question" (backtest-results.md:43-44).
4. **Medium.** The kickoff rulings (reading the replica and the production tool DB, no API key) are recorded only by the team (HACKATHON.md:166-168). Commit dates are set by the committer.
5. **Medium.** E18 ($262.81) comes from session transcripts that are not in the repo (HACKATHON.md:164). The logged model outputs #19, #24 and #38 (HACKATHON.md:81-90) also can't be checked.
6. **Low.** "917 passed, 1 skipped" (HACKATHON.md:50, :186) can't be rerun. I count 665 `[Fact]`/`[Theory]` attributes; with InlineData rows, 917 is plausible but unverified.
7. **Low.** The €4,000 B·Low is "a floor for the whole tool" (`fs:326`, one-pager.md:35). It rests on one GjirafaMall run of 3 Sep. "Floor" is an assertion; the August run was "not separable from restocking" (`fs:881-884`).

## Claims contradicted

8. **High. "Daily publishing" is not built.** HACKATHON.md:120-122 and one-pager.md:33 count lever 5 ("daily publishing", €396) as automation the agents run. But "the daily cycle doesn't publish regular prices: it ends in a checked export file for the push" (HACKATHON.md:155-156). Run week writes only discounts to the sandbox. Lever 5's mechanism exists only as a manual push.
9. **High. The 3 % campaign floor is not per SKU.** "Never under cost or a 3 % margin floor" (one-pager.md:14) and "minimum margin of at least 3 %" (HACKATHON.md:67) read as a per-SKU guarantee.
   - In code, a SKU's campaign price is bounded only by cost and today's price (CampaignPlanner.cs:100-105).
   - The 3 % is a *blended* plan margin, reached by making discounts shallower (CampaignPlanner.cs:125-167).
   - CampaignLimits still accepts a 0 % minimum (CampaignLimits.cs:61). Code raises it to 3 % later, in planning (CampaignPlanning.cs:112-118).
   - So individual SKUs can be planned at 0–3 % margin. CostWatcher's campaign lane then treats such a SKU as a breach (CostWatcher.cs:113-115).
10. **Medium. The "person's goal" in the chat is the model's number.** A goal typed in the chat is made binding as "the person's" (AgentChat.cs:244-246). But the stored value is Nova's own transcription (`b.GoalValue`, AgentChat.cs:213, :242), not a figure code parsed from the person's text. The team's code review kept this (code-review-1.md:19); HACKATHON.md doesn't mention it.
11. **Medium. Hours labelled "measured" are too low.** E16 says 3.9 attended hours, "measured" (numbers.json:273). The hours log shows the person working inside the "unattended" rows:
    - "the user's goal and context given in chat" (hours.md:25)
    - "The user asked why so low" (hours.md:24)
    - "the user's instruction" (hours.md:34)
    - the other session's requests (hours.md:38-39)

    The sheet assumes 1 hour for this (`fs:163-167`). The person was apparently present from 19:58 to 01:15.
12. **Medium. The team's own check contradicts its central case.** numbers.json:305 says "the layer-level year-over-year check does not support the uncontrolled two". Yet those two drive A·Mid and A·High (through E4) and B·Mid and B·High.
13. **Low. Stale history figures.** HACKATHON.md:23 says 114 commits and HACKATHON.md:27 says 244 files / 51,867 insertions. HEAD has 129 commits after the import and 268 files / 57,015 insertions.
14. **Low. Changed files under-listed.** HACKATHON.md:39 says 16 imported files changed "only where the plan says". The diff of Core and Data alone touches 18, including `PriceBandConfig.cs`, `User.cs`, `DbSeeder.cs` and `PricingToolDbContext.cs`, which the list doesn't name. Web `Program.cs`, `PortaOptions.cs` and `appsettings.json` also changed.
15. **Low. A cited method doesn't exist.** code-review-1.md:29 cites `PublisherOptions.Validate`; there is no `Validate(` in PricingTool.NopIntegration. The guard is `ShopFor` (PublisherOptions.cs:55-64).
16. **Low. The stored "Applied" count includes keeps.** "Alpha's weekly pass #2 changed 8" (one-pager.md:22) is right in the UI. But the stored `pass.Applied` still counts keeps as applied (FloorTargetAgent.cs:169).

## Rule compliance

17. **Medium. Timing.**
    - Commit 1 (`b3f19a6`, 16:50) is 1,237 lines of pre-kickoff plan and financial tooling (disclosed, HACKATHON.md:24-26).
    - Then come a 103-line production-DB copy script at 17:00 (`0410c08`) and 167 lines of replica export SQL at 17:04 (`ffc3af2`), 30 minutes after kickoff. Both look prepared in advance. They are data tooling, not agent code; the organisers should rule.
    - Every later commit is dated after kickoff, but committer dates prove nothing.
18. **Medium. Every number labelled.** Mostly yes; elasticities are now stated as assumed. But:
    - E16 is still "measured" (finding 11).
    - hours.md:37 says "the stored backtest run committed as CSV", which no commit contains, while HACKATHON.md:12 says `*.csv` stays out of git.
    - The cost inputs are "assumed" with no basis (finding 25).
19. **Medium. Live systems.**
    - The team's own rules summary says "nothing connects to prod DBs or the NOP replica during the build" (`rules:7`).
    - The build ran the engine on the replica (hours.md:10) and copied the deployed tool DB (`Copy-ToolDbKs.ps1`).
    - The permission rests on rulings recorded only by the team.
    - A wrong launch profile also migrated and seeded the main repo's *local* dev database; it is disclosed and its demo rows were left in place (dry-run-1.md:81-112).
20. **Pass. Secrets.**
    - `git log --all -G` for `password=|pwd=|sk-ant|AccountKey=|PRIVATE KEY|User ID=|Uid=` hits only docs that describe the check.
    - Every tracked connection string is `(localdb)`.
    - `Copy-ToolDbKs.ps1` takes its source as a parameter.
21. **Pass, one low note. Personal data.**
    - The sales SQL drops OrderId before the final select (`sales_lines_ks.sql:7`).
    - The tool DB copy pseudonymises who-columns to "team member" and blanks emails (Copy-ToolDbKs.ps1:16-39).
    - The employee's email is gone from HEAD (`80b8172`) but remains in import commit `9c977ca` (disclosed).
    - I could not open the xlsx files to check their metadata.
22. **Pass. Mocks shown.** Sandbox-only publishing, the `[INJECTED-TEST]` test DB and the unfinished items are listed (HACKATHON.md:137-170). I could not see the UI banner.
23. **Medium. AI that decides.** The models do decide, inside code limits:
    - Alpha's floors and targets;
    - the choice of campaign strategy;
    - Nova's briefs;
    - the run checker's escalations and fixes.

    But the money barely depends on them:
    - Alpha's lever 2 is worth €4 a month (E2).
    - Alpha's later passes changed 0 rules (hours.md:40).
    - The run checker sends at most 150 of about 6,000 flagged rows to the model; the rest are held by code (RunChecker.cs:84-89; hours.md:17).
    - Since the dry-run fix, the model can't lower any severity, only escalate or fix (RunChecker.cs:142-144).

    The AI is real, but almost none of the claimed euros come from its decisions (disclosed, HACKATHON.md:120-126).

## Financial sheet

24. **Pass. The arithmetic checks out.**
    - One-time: (3.9 + 1 + 160 + 8) × 50 + 262.81 = **8,907.81** ✓ (`fs:449`)
    - Running: 12 × (67.04 + 0 + 8 × 50) + 52 × 0.5 × 50 = **6,904.53** ✓
    - Full-benefit months: (13 − 2 − 2) + 2 × 0.25 = 9.5 ✓
    - A·Mid: 4,186 × 9.5 − 15,812.34 = **23,954.66** ✓; ROI 1.515 ✓
    - The payback grid and its interpolation are correct (`fs:516-631`).
    - The band thresholds match the team's Score bands tab.
    - The lever total ties to numbers.json (`fs:314`).
    - Running cost is taken for 12 months but benefit for only 9.5, which is conservative.
25. **Medium. Inputs without a source.**
    - Costs: 1 h in unattended blocks, 160 h to production, 8 h training, 8 h a month maintenance, 0.5 h a week review, servers €0, setup €0, $1 = €1 (`fs:163-234`).
    - Ramp: pilot start month 2, 2 months long, at 25 % (`fs:204-217`).
    - AI volumes: every call count (`fs:376-408`).
    - Model assumptions: the elasticities −2 / −1 / −0.5 and E7's half and double slope.
    - Servers at €0 assume production runs on k8s "with Claude Code's OAuth token as a secret" (`fs:193`). That is neither built nor costed, and the hackathon actually ran on a logged-in laptop.
26. **High. A 12-month window that mixes baselines.**
    - The fix for "one August event × 11" moved levers 1 and 3 to a 12-month window, Oct 2025 – Sep 2026 (backtest-results.md:4-5).
    - The calculator has only run since July 2026 (DECLARATION.md:7). Before then the loss block catches 18–81 % of Mall's loss; in the calculator era, 4–37 % (backtest-results.md:41-42).
    - So lever 1 grew from €196 to €880 (dry-run-1.md:57) by scoring against a baseline that no longer exists, which breaks framing A's "today's process carries on" (`rules:28`).
    - Net effect of the "fix" on levers 1 + 3: €1,723 → €1,650, almost unchanged.
27. **High. Double counting in framing B.**
    - B adds B0 (the calculator's measured Aug–Sep gain, which comes mostly from lifting under-floor prices, the same sales lever 1 targets) to lever 1's 12-month average, which is dominated by months before the calculator removed those losses.
    - B·Mid also applies the same uncontrolled 5.9 % rate to Kosovo (B0) and to MK/AL (E4). These are different bases, but they share one unsupported assumption (finding 12).
    - B's baseline ("without v2 the manual pushes stop") is stated as a position (`rules:30`), not shown.
28. **Medium. The holdout is counted as benefit.**
    - The backtest never excludes holdout categories: "holdout" appears nowhere in `PricingTool.Agent/Backtest/`.
    - But holdout categories are "never changed by an agent" (SafetyLimits.cs:42-43) and are left out of every export: 6,643 and 17,853 prices on 30 Oct (hours.md:17).
    - The pilot ask keeps them as the control (one-pager.md:59-60), so roughly 12–17 % of each layer's benefit can't arrive in year 1.
29. **Pass. Revenue as margin: none.** Gains are gross profit at booked COGS (LeverBacktest.cs:196-199). One caveat: lever 1 takes the full avoided loss at every elasticity (LeverBacktest.cs:106) and ignores lost baskets (disclosed).
30. **Pass. Hours saved as money: not counted** (`fs:140`).
31. **Medium. E7's range is asymmetric.** Lever 7 runs €703 / €783 / €1,744. The low case is only 10 % under central, while the planner is uncalibrated: no leave-one-out factor (E10), actual vs plan ×0.45–×3.90 (backtest-results.md:75), and the Për ty reproduction fails (E11).
32. **Medium. Costs missing.**
    - Writing regular prices back, or getting the commercial owner's decision on superseding team deals, which levers 1, 3 and 5 need (finding 2).
    - Measuring the pilot.
    - The data session's hours.
    - The person's real presence during the unattended blocks (finding 11), which adds only about €265.

## AI decides / code enforces

33. **Pass. Alpha → floors and targets.**
    - The model's answer goes through `SafetyLimits.Evaluate` (FloorTargetAgent.cs:149-154). Its checks (SafetyLimits.cs:42-81):
      - holdout and thin-data rows kept;
      - malformed answers rejected;
      - out-of-range values rejected (floor 0–60 %, target 0–80 %);
      - target below floor rejected;
      - floor under the hard minimum (5 % / 4 %) rejected;
      - paused rows held;
      - more than 1 point in 7 days held.
    - Only Applied, non-keep decisions are written (FloorTargetAgent.cs:167).
    - A person's approval lifts only the step limit (FloorTargetAgent.cs:204-221).
34. **Pass, with finding 9. Campaign prices are computed by code.**
    - Alpha must choose a strategy code computed (CampaignPlanning.cs:373).
    - The depth cap is enforced (CampaignPlanning.cs:202-211).
    - A person's goal can't be changed by Nova (:205-210), and a plan short of it is held (:160-166).
    - A plan with no discounted SKU is never published (commit `9203aa7`).
    - The weak spots: the per-SKU margin (finding 9) and a "person's goal" that is really the model's (finding 10).
35. **Pass. Run checker.** A model fix is accepted only if every rule re-checks at Info or below (RunChecker.cs:131-138). A rule's block stays a block, and the model can't lower any severity (:142-144). Warn and Block rows are held out of the export, and a blocked run exports nothing (CheckedExport.cs:103-111).
36. **Pass, low. Publisher.**
    - With `Target=Sandbox`, any schema without the `shop_sandbox_` prefix throws (PublisherOptions.cs:58-62). All three configs say Sandbox.
    - Run week refuses a non-sandbox target (RunWeekLoop.cs:123).
    - Run week and the chat are Manager-only (AgentController.cs:92, :120).
    - Low: in Sandbox mode a configured shop `ConnectionString` is still honoured (:63), so the guard is on the schema name, not the server.

## Other findings

37. **Medium. The pilot doesn't match the sheet.** The pilot ask covers a few Kosovo categories, one canary first (one-pager.md:57-60). The sheet assumes full scope from month 4: both KS layers plus four MK/AL layers whose daily cycle doesn't run yet (Run week covers "only the cycle's two layers", code-review-1.md:18; MK/AL data loads are in the 160 h).
38. **Low. A known gap is disclosed only in a side document.** The "goal is always the person's" code-review finding was kept and disclosed only in code-review-1.md.
39. **Low. The demo depends on fixes made minutes earlier.** Run week (`2837618`, 01:07) and its review fixes (`f5a1b22`, 01:15) landed in the last 10 minutes. Run week #1 is the only recorded run.

---

## Dry run 1 findings

| # | Sev. (theirs) | Status now | Evidence |
|---|---|---|---|
| 1 | high | **Disclosed only** | HACKATHON.md:9-12 now says what needs the DB. The promised CSV is not in git (hours.md:37 vs `git log --all -- "*.csv"`: empty) |
| 2 | high | **Not resolved / disclosed** | numbers.json:307, :320 "not in this repo"; HACKATHON.md:161-163 |
| 3 | med | **Not resolved** (minor) | €4.0k vs a midpoint of €3.9k is fair, but "floor for the whole tool" is still an assertion (`fs:326`) |
| 4 | med | **Partly** | The one-pager names runs #1100/#1101 (one-pager.md:21-22). `pass.Applied` still counts keeps (FloorTargetAgent.cs:169); the UI was fixed per hours.md:20 (not checkable) |
| 5 | low | **Resolved** (wording) | backtest-results.md:47-49. "All 8 are +1" needs the DB |
| 6 | med | **Partly** | "never yet run against the live shop" added (one-pager.md:18-19). "Going live is configuration" is still untrue for the money levers: raises need the regular-price push (finding 2) |
| 7 | med | **Disclosed only** | HACKATHON.md:166-168 |
| 8 | low | **Partly** | Test counts aligned at 917 (E17). Transcripts and logged outputs still not checkable |
| 9 | high | **Disclosed only** | HACKATHON.md:115-118 states the dependency and the €509 controlled value. A·Mid and A·High still use €1,353 / €1,654 (`fs:278`) |
| 10 | high | **Disclosed only** | Called "the organisers' call" (`fs:4`; one-pager.md:42). The numbers are unchanged; "no past euro" is wording, not substance |
| 11 | high | **Partly** | Ramp added (`fs:204-221`: 2 pilot months at 25 %, 9.5 months). Full six-layer scope from month 4 and the holdout's share still counted (findings 28, 37) |
| 12 | med | **Partly** | 1 h assumed added (`fs:163-167`). E16 is still labelled "measured" (numbers.json:273); the log shows more involvement (finding 11) |
| 13 | med | **Resolved** | numbers.json:300-304; `fs:337` |
| 14 | med | **Resolved** (structure) | One Works rating (`fs:364-367`). The 4 itself is still unsupported (my score: 2) |
| 15 | med | **Resolved** | Evidence is now 3 with an honest reason (`fs:356-363`) |
| 16 | med | **Partly** | The note now says k8s with an OAuth token (`fs:193`). Not built, not costed |
| 17 | low | **Partly** | HACKATHON.md:39, :43 now list the changes, but under-list them (finding 14) |
| 18 | low | **Partly** | The old figures were fixed; new stale ones appeared (finding 13) |
| 19 | med | **Disclosed only** | HACKATHON.md:24-26. Early data tooling at 17:00–17:04 also looks prepared (finding 17) |
| 20 | low | **Pass, unchanged** | Import labelled (`9c977ca`) |
| 21 | med-high | **Disclosed only** (plus a new incident) | Rulings recorded by the team; the dev-DB migration of 31 Oct is disclosed (HACKATHON.md:179) |
| 22 | low | **Partly** | Email removed from HEAD (`80b8172`; `git grep` clean). Still in `9c977ca`. I could not check the xlsx metadata; commit 1's draft keeps it in history |
| 23 | med | **Partly** | Elasticities are now "assumed" in every method; E20 high fixed. E16 still "measured"; the CSV claim is false |
| 24 | pass | **Pass** | Unchanged |
| 25 | med | **Partly** | Costs raised (160 h, 8 h, 8 h/mo) with notes, but still "assumed" with no basis; the ramp inputs are also unsourced |
| 26 | pass | **Pass** | Re-verified (finding 24) |
| 27 | high | **Partly; the fix introduced a new error** | Lever 3 went to 12 months (€1,527 → €770), but lever 1 rose €196 → €880 on pre-calculator months. Levers 1 + 3 barely moved (€1,723 → €1,650); levers 2, 5 and 6 still rest on two months (finding 26) |
| 28 | high | **Not resolved** | Kept (dry-run-1.md:64). A further overlap: B0 against the 12-month lever 1 (finding 27) |
| 29 | low-med | **Disclosed only** | HACKATHON.md:165 |
| 30 | pass | **Pass** | `fs:140` |
| 31 | med | **Partly** | E7 is ranged and A·High is now €37.7k. The range is asymmetric and E4 is still uncontrolled in A·Mid/High (finding 31) |
| 32 | med | **Partly** | Code review and sign-off added. Pilot measurement, the data session and the regular-price write-back are missing |
| 33 | high | **Disclosed only** | HACKATHON.md:120-126. Lever 2 is still €4 |
| 34 | pass | **Pass** | SafetyLimits.cs:42-81 unchanged in substance |
| 35 | med-high | **Partly** | A 3 % floor is now in code on the brief and review rounds (CampaignPlanning.cs:112-118, :197-201; test CampaignPlanningTests.cs:405-406). It is blended, not per SKU (CampaignPlanner.cs:101-105, :125-167), and CampaignLimits still accepts 0 (CampaignLimits.cs:61) |
| 36 | med | **Resolved** | RunChecker.cs:142-144: the model can't lower any severity; a rule block stays a block |
| 37 | low | **Resolved** (wording) | HACKATHON.md:71 now ties the guard to `Target = Sandbox` |
| 38 | med | **Disclosed only** | HACKATHON.md:169-170 |
| 39 | med | **Partly** | E7 is ranged; Për ty still fails (E11) and there is still no leave-one-out factor (E10) |
| 40 | low | **Disclosed, unchanged** | DECLARATION.md:40-41 |

**Summary.** 3 resolved outright (#13, #15, #36), 3 more resolved in wording or structure (#5, #14, #37), 7 pass unchanged, 17 partly, 9 disclosed only, 2 not resolved. The structural problems remain:

- framing B is the calculator's money;
- E4 borrows uncontrolled rates;
- the AI's decisions carry almost none of the euros.

A new problem now outweighs several of the fixes: the money levers need price raises that the built publisher cannot make.