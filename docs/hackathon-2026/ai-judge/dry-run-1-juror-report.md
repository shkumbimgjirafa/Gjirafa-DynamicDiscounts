# Juror report: Pricing Tool v2 (Track A, Money)

**Basis.** I read the repository at `eb6572c` (120 commits), DECLARATION.md, HACKATHON.md, the one-pager, numbers.json, the sheet dump (`../material/financial-sheet.txt`, cited as `fs.txt`) and the team's rules summary (`../material/rules-summary.md`, cited as `rules`). The official rules and jury sheet were not available.

- The net-benefit bands for ratings 4 and 5 come from `rules:16`.
- The other net-benefit bands and all ROI and payback bands come only from the team's own "Score bands" tab (`fs.txt:598-626`). I could not check them against the official rules.
- The hackathon database is not in the repo, so I could not rerun any figure, test or backtest. Per the brief, anything that needs the database gets no credit.

---

## Scores

| Row (weight) | Team's own (A·Mid / B·Mid) | **Framing A, my score** | Framing B, my score |
|---|---|---|---|
| Net benefit, year 1 (40) | 3 / 5 | **2** | 4 |
| ROI (10) | 5 / 5 | **4** | 5 |
| Payback (10) | 4 / 4 | **3** | 4 |
| Evidence (20) | 4 / 4 | **2** | 1 |
| Works (20) | 4 / 4 | **2** (3 if the demo shows it) | 2 (3) |
| **Points /100** | 74 / 90 | **46** (50) | 62 (66) |

**I would pick framing A.**
- Framing B's extra money is the effect of the declared, pre-existing calculator, which has no AI in it (DECLARATION.md:9).
- The declaration says that effect is "**not claimed** as hackathon benefit" (DECLARATION.md:14-16).
- The calculator already runs today, with people applying its prices by hand (DECLARATION.md:7-13). So the strict baseline already earns that money.

**Net benefit, A = 2.** The team's A·Mid is €35,679. Two corrections bring it down:
- Take the one-off August cost event out of lever 3: `backtest-results.md:48-49` says that without it Mall's lever 3 is about €150–200. That moves E3 from €1,527 to about €387.
- Use only the controlled rate for E4: €509 instead of €1,349.

That gives **€2,275 a month**. €2,275 × 11 − €11,126 = **€13,899**, which is band 2 on the team's own bands. Starting on one layer, as the pilot proposes, would push it lower. If you accept the team's numbers unchanged, A·Mid scores 3.

**Net benefit, B = 4.** I accept only B·Low, the controlled figure: €65k as stated, about €56k after the same corrections. B·Mid and B·High rest on before/after figures that the team's own year-over-year check does not support (finding 9).

**ROI, A = 4.** The corrected figure is €13,899 / €11,126 = 125%, band 100–200%. ROI stays high in every case because the costs are small.

**Payback, A = 3.** The corrected figure is (€6,620 + €2,275) / (€2,275 − €375.5) = **4.7 months**, band 3–6.

**Evidence, A = 2.**
- The backtest code exists (`LeverBacktest.cs`), but its data and output are not in the repo.
- 68% of A·Mid is one cost shock (E3) plus a rate imported from the calculator (E4).
- The elasticities are assumed.
- Nothing has been measured live: "The money ledger has measured nothing yet" (HACKATHON.md:133).

**Evidence, B = 1.** Framing B's rate is "Main repo analysis… script: not in this repo" (numbers.json:306-307).

**Works = 2 on paper.** I could not run anything. The code paths and test names exist, but "works end to end on real data" is a behaviour claim that needs the demo. **If the demo shows the Kosovo daily cycle (engine run → run check → checked export → sandbox publish) end to end on real data, raise it to 3.** Below 3 means no prize (`rules:20`), so this decides eligibility.

---

## Biggest unproven assumption

**That what the backtest found in Aug–Sep 2026 will repeat every month for 11 months, at full scope, from month 2.**

- **E3 (36% of A·Mid)** is mostly one supplier cost shock in the week of 3 August (`backtest-results.md:45-49`).
- **E4 (32%)** applies the calculator's mostly uncontrolled Kosovo rate to four Macedonia/Albania (MK/AL) layers. The system does not run on those layers yet (`fs.txt:167` lists "MK/AL data loads" as remaining build), and all four were already pushed by hand on 21 Oct (`backtest-results.md:30-31`).
- **The pilot** goes live on one Kosovo layer and one canary category, with a decision at week 8 (one-pager.md:50-52).

Nothing is measured live. The team names elasticity as its biggest assumption (one-pager.md:46-48). Elasticity matters, but recurrence matters more.

---

## Claims not verifiable

1. **High.** HACKATHON.md:9: "Everything below can be checked from this repository and the hackathon database." The database is not supplied. So every runtime "measured" figure (E1–E15, E17, E19), the 37 calls / $4.04 (HACKATHON.md:67) and `hk.DataSources: 32 rows` (HACKATHON.md:140) cannot be checked.
2. **High.** E20 and E21 have "script: not in this repo" (numbers.json:306-307, 318-320). Framing B and E4 rest on E20 (`fs.txt:309-333`).
3. **Medium.** one-pager.md:31 calls the €4,000 "a floor". The source is one Gjirafa­Mall run of 3 Sep, worth "+€115–141/day ≈ €3.5–4.3k/month" (`fs.txt:310`), while the August run was "not separable from restocking" (`fs.txt:847`). The team picked €4.0k, near the top of that range, and "floor for the whole tool" is an assertion.
4. **Medium.** one-pager.md:20-23: engine runs of 130k and 511k SKUs, "Alpha's weekly pass applied 8 category rules", the Black Friday plan held. hours.md:10 records run #1099 at 516,122 SKUs, not 511k. Also, `pass.Applied` counts "keep" decisions as applied (FloorTargetAgent.cs:169).
5. **Low.** backtest-results.md:42 says "Alpha's 8 rules… raise floors 1–3 points", while code limits a move to 1 point per 7 days (SafetyLimits.cs:74-79). This is probably measured against older hand-set floors, but I cannot reconcile it without the database.
6. **Medium.** "Go-live is configuration" (HACKATHON.md:119, one-pager.md:18). The live write path has never touched a real shop database, only a sandbox said to have the "exact live DDL".
7. **Medium.** The kickoff rulings (replica allowed, tool DB allowed, no API key) are recorded only by the team (kickoff-plan.md:113-126). Commit dates are set by the committer; there is no push log.
8. **Low.**
   - The 909 passing tests (HACKATHON.md:44) could not be run; failure-cases.md:3 says 900.
   - The logged model responses #19, #24 and #38 (HACKATHON.md:74-84) could not be checked.
   - E18 depends on session transcripts that are not in the repo.
   - "The worked example in the rules scores 76" (`fs.txt:124`) could not be checked.

## Claims contradicted

9. **High.** The team says framing B's uncontrolled figures are "never added into these figures" (HACKATHON.md:104-106). But E4, which is inside framing A, uses exactly E20's low, mid and high values as shares of Kosovo GP: 4,000 / 10,632 / 13,000 ÷ 180,411 = 2.2 / 5.9 / 7.2% (numbers.json:100; backtest-results.md:32-33). The team also writes that "the layer-level YoY check does not support the uncontrolled two on GP" (numbers.json:305; `fs.txt:322`). Yet those two drive A·Mid, A·High, B·Mid and B·High.
10. **High.** Framing B contradicts the declaration (DECLARATION.md:14-16, "not claimed"). In B·Mid, B0 = €10,632 × 11 = €116,952, which is **71%** of the €163,757 first-year benefit. Calling it "no past euro" (one-pager.md:32-33) does not change the fact that the money is the declared, non-AI calculator's effect.
11. **High.** The pilot ask covers one Kosovo layer and one canary category, with a decision at week 8 on the rest (one-pager.md:50-52). The sheet counts the full benefit (both Kosovo layers plus four MK/AL layers) from month 2 for 11 months (`fs.txt:199-205, 417-438`).
12. **Medium.** Hours rows marked "0 (unattended, AI)" also record the user taking part:
    - "The user asked why so low… dates asked of the user" (hours.md:24)
    - "the user's goal and context given in chat" (hours.md:25)
    - "confirmed by the user" (hours.md:19)
    - "The user found the goal too low" (HACKATHON.md:76)

    So E16's 3.9 h, labelled "measured", is too low. The money involved is small (19:58–00:18 is at most about €215), but the label is wrong.
13. **Medium.** The Evidence tab labels the season-adjusted case "estimated, not controlled" (`fs.txt:836-837`). The input that feeds the formulas labels the same number "measured" (`fs.txt:321`; numbers.json:303).
14. **Medium.** The sheet ties Works 3/4/5 to the Low/Mid/High columns (`fs.txt:96-102, 354-359`), but Works does not depend on elasticity. Works 5 also requires that "normal, unusual and failure cases all work", while HACKATHON.md:122-133 lists:
    - a failing Për ty reproduction test (the agents replaying a past hand-made campaign plan)
    - the Monday report email, not built
    - the start-up task, not registered
    - a money ledger that has measured nothing
15. **Medium.** The Evidence 4 reasoning says "every input has an ID and a script" (`fs.txt:347`). That does not hold for E20/E21, or for the assumed cost inputs (finding 25).
16. **Medium.** The sheet says servers cost €0 because it "runs on the existing k8s cluster" (`fs.txt:177, 187`). The actual live path is "this laptop" (kickoff-plan.md:162; GO-LIVE-LAPTOP.md). The agents also run on a logged-in subscription, not an API key (kickoff-plan.md:121-124), which is not a k8s deployment.
17. **Low.** HACKATHON.md:37 says "reused unchanged: the calculator's engine", but `git diff 9c977ca..HEAD` shows changes to `MarginTargetService.cs`, `PricingRunOrchestrator.cs`, `BandConfigProvider.cs`, `PriceBandConfig.cs` and `RoundingService.cs`. Line 33 discloses them, so this is internally inconsistent rather than hidden.
18. **Low.** Stale figures in HACKATHON.md:
    - $220.94 / $142.65 (HACKATHON.md:68-69) vs $224.91 / $150.35 in numbers.json:253-255 and the sheet
    - "18 unattended blocks" (HACKATHON.md:144) vs 19 (numbers.json:269)

## Rule compliance

19. **Medium. Timing.** Every commit after the import has an author date between 16:52 on 30 Oct and 00:19 on 31 Oct. Commit 1 (`b3f19a6`, 16:50, 20 minutes after kickoff) adds 1,237 lines, including:
    - `build_financial_sheet.py` (356 lines)
    - `excel_recalc.ps1`
    - a 681-line plan
    - a draft financial sheet

    These were clearly prepared before kickoff. They are tooling for the financial case, not solution code, but the organisers should rule on it. Author dates are not proof of timing.
20. **Low. Declared vs new.** Commit `9c977ca` is a labelled import of `b184ff55…`. I cannot compare it with the main repo. Changes to the imported code are limited to 20 files. Counting the imported calculator's effect as benefit is the real problem (finding 10).
21. **Medium-high. Live systems.**
    - `rules:7` says "No live systems touched", and the team's own first-commit CLAUDE.md said "Never connect to the prod tool DB, the NOP replica".
    - The build then ran the engine on the NOP replica (hours.md:10) and copied the **deployed production tool database** (`0410c08`, `Copy-ToolDbKs.ps1`, committed 17:00:56).
    - The ruling "Tool DB: reading is allowed" was only written down at 17:04:55, inside an unrelated API-key commit (`ffc3af2`; kickoff-plan.md:118-120).

    The organisers need to confirm both permissions.
22. **Pass, with two low notes. Secrets and personal data.**
    - I searched the full history for `password=`, `pwd=`, `user id=`, `sk-ant`, `AccountKey=`, private keys and `Bearer` tokens and found nothing.
    - Every tracked connection string points at `(localdb)`; the copy script takes its source as a parameter.
    - The sales export keeps OrderId only in a temp table and leaves it out of the final select (`sales_lines_ks.sql:57-61`). The loaded data itself is not in git, so I could not check it.
    - **Low:** an employee's work email is the default admin (`PricingTool.Web/appsettings.json:24`, `PortaOptions.cs:38`, `README.md:345`). It was imported, but it is in the tree.
    - **Low:** an Office user's name stays in the metadata of the draft xlsx in commit 1 (the team admits this at hours.md:36).
23. **Medium. Every number labelled.** Labels are mostly present, but several are too strong:
    - E1–E7 are labelled "estimated", which hides that the elasticities are assumed.
    - The season-adjusted E20 is labelled "measured" (finding 13).
    - E16 is labelled "measured" (finding 12).
    - Many cost inputs are "assumed" with no source (finding 25).
24. **Pass. Mocks shown.** HACKATHON.md:116-133 lists sandbox-only publishing, injected tests in a separate database, and the unfinished items. I could not see the UI banner.

## Financial sheet

25. **Medium. Inputs without a source** (Finance would strike them):
    - remaining build 120 h, training 4 h, maintenance 4 h/month, exception review 0.5 h/week
    - first live month = 2
    - setup and servers €0
    - USD→EUR 1:1
    - all call volumes (`fs.txt:163-217, 368-401`)
    - the elasticity cases
    - lever thresholds, such as the "assumed ceiling" (backtest-results.md:42)

    Four hours a month to maintain two LLM agents and a shop-database writer is low. At 16 h/month the cost rises by €7,200 a year.
26. **Low. Arithmetic checks out.**
    - One-time cost: (3.9 + 120 + 4) × 50 + 224.91 = 6,619.91 ✓
    - Running cost: 12 × (67.18 + 4 × 50) + 52 × 0.5 × 50 = 4,506.17 ✓
    - A·Mid net: 4,255 × 11 − 11,126.08 = 35,678.92 ✓; ROI 3.21 ✓
    - Payback: (6,619.91 + 4,255) / (4,255 − 375.51) = 2.80 ✓
    - The month grid and the cross-check against numbers.json (`fs.txt:298`) match.
    - The formula counts margin, takes running costs for 12 months but benefit for 11, and starts payback in the build month. All of these are conservative.
27. **High. A one-off counted as monthly.** A two-month window (Aug–Sep) is multiplied by 11, with no seasonality. Most of E3 is a single cost event; this is the core of the biggest assumption above.
28. **High. Baseline counted as benefit / double counting.**
    - Framing B's B0 is already in the baseline (finding 10).
    - B·Mid applies the same uncontrolled 5.9% rate twice: to Kosovo as B0, and to MK/AL through E4.
    - Inside framing A, levers 1, 2, 3 and 5 count each sales line at most once (`continue` at LeverBacktest.cs:107, 124, 138, 147) ✓.
29. **Low-medium. Revenue as margin: none found.** Gains are computed on GP at booked cost of goods (LeverBacktest.cs:197-201). One caveat: lever 1 counts the full avoided loss at every elasticity (LeverBacktest.cs:106), which assumes blocking a sale has no cost (no lost customer, no lost basket).
30. **Pass. Hours saved as money:** not counted (`fs.txt:140`).
31. **Medium. How fragile the scores are.**
    - A·High clears €40k by only €1,916, and only because of the uncontrolled E4 high value. With the controlled €509 it is €29,321, band 3.
    - E7 (€783) has the same value in all three cases, even though the planner's own estimates are uncalibrated (actual ÷ plan ranged ×0.45–×3.90; backtest-results.md:66-70).
    - Lever 2 is in-sample (backtest-results.md:43).
32. **Medium. Costs left out:**
    - the commercial owner's time to confirm the limits (CampaignLimits.cs:5-6: "Assumed values, to confirm with the commercial owner")
    - pilot measurement
    - review of about 53k lines of AI-written code (only 3.9 attended hours)
    - the data session's hours

## AI decides / code enforces

33. **High. The money doesn't come from the AI's decisions.**
    - The only lever driven by a model decision is lever 2, Alpha's per-category floors: **€4 a month** (E2).
    - Levers 1, 3 and 5 are deterministic code: the loss block, the cost watcher ("nothing: no model", HACKATHON.md:63) and cadence. Together they are €2,119.
    - E4 is the calculator's rate applied to new layers.
    - E7 replays the code planner's depth per SKU, not Alpha's choice of strategy.

    So about 0.1% of framing A's money comes from model decisions. If the jury links "AI must decide" (`rules:6`) to the benefit, this matters.
34. **Pass. Alpha → floors and targets.** The model's answer goes through `SafetyLimits.Evaluate` (FloorTargetAgent.cs:149-150), and only Applied decisions are written (:167). The limits are in code (SafetyLimits.cs:42-81):
    - holdout and thin-data rows are kept unchanged
    - out-of-range values are rejected (floor 0–60%, target 0–80%)
    - the target can't go below the floor
    - the floor can't go below the hard minimum (5% / 4%)
    - paused rows are held
    - a move is capped at 1 point per 7 days

    Prices then come from the engine. Code stands between the model and the price. Note that autonomous mode writes with no person in the loop (:152).
35. **Medium-high. The campaign minimum margin is chosen by the model and has no floor in code.**
    - Nova sets the brief's minimum margin, and `CampaignLimits` accepts 0 (CampaignLimits.cs:57).
    - In review rounds Nova can lower it to any value ≥ 0 (CampaignPlanning.cs:385). Only values ≥ 100 are rejected (:181), and the adjusted brief is not re-checked by `CampaignLimits`.
    - What code does enforce: price ≥ cost and below today's price (CampaignPlanner.cs:100-105), depth ≤ 25% (CampaignPlanning.cs:187-196), a person's chat rule on minimum margin (:107-124) and a person's goal (:190-195).
    - Band floors do not apply to campaign prices.
    - A brief from the chat is planned with `publish: true` (AgentChat.cs:251). The only gate is the 3-day lead time / veto window.
    - I found no test for Nova lowering the minimum margin.
36. **Medium. The run checker can lift a block that code imposed.**
    - Pass: a fix price from the model becomes an exported price only if every rule re-checks at Info level or below (RunChecker.cs:130-137 → CheckedExport.cs:102, 108).
    - Problem: the model can downgrade any rule-level Block (below cost, x10 jump, cost at the wrong scale) to Warn by giving any non-empty reason (RunChecker.cs:142). The row itself is still held back (CheckedExport.cs:101), but the run is no longer blocked, so the rest of a run that may have a data-scale error gets exported.
37. **Pass, low. Strategy and publisher.** Alpha's strategy must be one of the strategies code computed (CampaignPlanning.cs:352) ✓. The sandbox guard is a config check on the schema prefix (PublisherOptions.cs:58-63): HACKATHON.md:118's "refuses any schema outside `shop_sandbox_*`" is true only while `Target=Sandbox`.

## Other findings

38. **Medium.** The team's internal "10% bar" goal needs "8,000 Claude requests in 30 days" (kickoff-plan.md:142). The sheet's AI running cost assumes about 290 calls a month (`fs.txt:368-401`). Either the cost line is too low by an order of magnitude, or calls would be padded to hit the bar.
39. **Medium.** The Për ty reproduction test fails (E11), and leave-one-out calibration is unavailable (E10). The same planner arithmetic still earns E7 a fixed €783 a month with no range.
40. **Low.** The registration promised "with a person approving its changes". The demo runs in autonomous mode. This is disclosed (DECLARATION.md:40-41).