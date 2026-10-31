# Juror report: Pricing Tool v2 (Track A, Money), review of HEAD `f2de134`

**Scope and limits.** I read DECLARATION.md, HACKATHON.md, the one-pager, numbers.json, the sheet dump (cited as `fs:`), the team's rules summary (`rules:`), the E20 SQL, the dry-run 3 report and the team's answer, hours.md, the code paths below and the git history (178 commits after the import; 8 since `d980294`).

- I could not run the app, the tests, the SQL or any Python, and I could not open the xlsx.
- The only scoring bands I can trace to the rules are the net-benefit bands for ratings 4 and 5 (`rules:16`). Every other band (net benefit 1–3, all ROI and payback bands) comes from the team's own "Score bands" tab (`fs:646-694`).
- Wherever a rule is cited, it comes from the team's summary, not the official document.

Fixed costs used in every recalculation below (verified from the sheet): one-time €9,038.76, running €576.72 a month, total €15,959.42, 9.5 full-benefit months, holdout factor 0.89.

---

## Scores

| Row (weight) | Team B·Mid | **Framing B at the claim** (B0 €10,409, granted for argument) | **Framing B at the controlled measurement** (B0 €4,402) | Team A·Mid | **Framing A, cross-check** |
|---|---|---|---|---|---|
| Net benefit (40) | 5 (€107,205) | **4** | **4** (≈ €45–50k) | 3 | **2** (≈ €12.7k) |
| ROI (10) | 5 | **5** | **5** (≈ 280 %) | 4 | **3** (80 %) |
| Payback (10) | 3 | **3** (3.3–3.4 months) | **3** (≈ 4.3 months) | 3 | **2** (6.8 months) |
| Evidence (20) | 3 | **2** | **2** | 3 | **2** |
| Works (20) | 4 | **2** (3 if Run week is shown live) | **2** (3) | 4 | **2** (3) |
| **Total /100** | **84** | **64 (72)** | **64 (72)** | **66** | **42 (46)** |

**Framing B at the claim: net benefit 4, not 5.** The 5 needs at least €12,206 a month (`fs:137`); the claim is €12,965, 6 % above it. Any one of three things the repository itself supports drops it below €100k:

- **Lever 3 without its single August cost event.** The snapshot has GjirafaMall's lever 3 at €2,554 in August and €184 in September (`backtest-snapshot.json:647, :674`). Scored on September alone, the month is €11,957 and net benefit €97.6k.
- **One more pilot month before full scope.** That gives 8.75 full-benefit months and €97.5k. Full six-layer scope from month 4 is assumed: the pilot ask is "a few categories" in Kosovo (one-pager.md:92-95), and the MK/AL cycle doesn't run yet.
- **B0 about 6 % lower,** which is well inside its uncertainty (finding 10).

**Framing B at the controlled measurement.** The team's B·Low (€5,694 a month, net €38,136, rating 3) changes two things at once: it swaps in the controlled B0 and also drops every lever to elasticity −2 (`fs:442`). Holding the levers at their central case, with lever 4 at the controlled rate, gives €6,938 a month and €49.9k net. With lever 3 at the team's own 12-month figure (€770, hours.md:37) it is €45.2k; with lever 3 on September alone, €40.4k. All three are rating 4, the last one barely.

**Framing A, corrected.** I changed one input: lever 4 at E20's controlled rate (€560 instead of €1,325), as HACKATHON.md:164-166 allows.

- €3,393 × 0.89 = €3,020 a month; net €12,729 (rating 2), ROI 80 % (3), payback 6.8 months (2).
- Lever 3 at €770 as well: net €8.0k, still 2 / 3 / 2.
- Lever 3 on September alone: net €3.1k, 1 / 1 / 1.

**Evidence: 2 everywhere.**
- What's committed and consistent: the backtest snapshot, the E20/E21 SQL and the code.
- None of it can be rerun here.
- The elasticities are assumed.
- The headline B0 is an uncontrolled before/after that the team's own controlled run, on the same approval, cuts roughly in half (finding 10).
- Nothing has been measured live (the money ledger is empty).

**Works: 2 on paper, 3 if demoed.**
- Nothing could be run.
- Raise it to 3 if a full Run week on real data is shown live: Alpha → engine → check → sandbox publish → read-back.
- It can't reach 4:
  - the Për ty reproduction fails (E11);
  - the ledger is empty;
  - nothing has been published live;
  - 43 % of checked prices don't reach even the sandbox shelf with the default switch.

### Do I accept €10,409 a month as the rate? No.

1. **The team's own controlled run on the same approval gives about half.**
   - GjirafaMall's September before/after is €7,461.
   - The controlled run on approval #25, which is most of that month, gives €3,263 against the products it kept and €4,088 against products not in the approval (`fs:860`).
   - The "shoppers move to kept products" argument (HACKATHON.md:123-126) doesn't explain the gap: the not-in-approval control has no such link to the changed products and still gives only €4,088.
2. **August rests on approval #12, which the team's own material calls confounded.**
   - The sheet: "not separable from restocking: units rose 285 % … 86–97 % of after-GP from SKUs that didn't sell before" (`fs:936-938`). The SQL says the same (`e20-test-phase.sql:13-14`).
   - Prompt 13 tells the documents not to quote #12's controlled run for that reason (`prompts/13-b-headline-10k.md:39-40`), yet #12's before/after is the bulk of August's €8,551.
3. **Gjirafa50's B0 is not measured in August and September.** Approval #1's 30-day lift around 21 July is spread over 21 Jul–6 Oct (`e20-test-phase.sql:30, :57-59`).
4. **The figures' own method text still says the year-over-year check "does not support the uncontrolled two"** (numbers.json:501, generated 10:29, after Prompt 13).

**What would change my mind:**
- The per-approval output of `e20-test-phase.sql` result set 1 committed, showing how much comes from the dead-stock sets (#6, #7, #13, #19, #22) and from #12.
- A restock-robust control for #12: only SKUs with stock and sales in both windows.
- Controlled runs for the October approvals (#30, #32, #35, #37) and for a Gjirafa50 approval inside Aug–Sep that together approach €10k a month.
- Above all, the pilot's holdout difference-in-differences from the money ledger.
- Separately, to count B0 as *new* benefit at all: evidence that without v2 the calculator's pushes would stop.

---

## Biggest unproven assumption

**That B0 (€10,409 a month) is the calculator's causal effect *and* is lost without v2.**

- B0 is 71 % of B·Mid's monthly benefit (€9,264 of €12,965). Lever 4 borrows the same rate.
- Causality is contradicted by the team's own controls (finding 10).
- Incrementality is only asserted (`rules:30`, kickoff-plan.md:44 "without v2 nothing secures that profit"). The record shows the manual process carrying on and speeding up: four approvals in October (#30, #32, #35, #37; `fs:908`) and pushes on all four MK/AL layers on 21 Oct (backtest-results.md:43-44).

---

## Claims not verifiable

1. **High.** Every runtime figure needs `PricingToolHackathon`, which isn't supplied (HACKATHON.md:18-21). That covers:
   - E1–E19 and E21–E26;
   - the cost watcher's 293 moves and 54 holds (HACKATHON.md:7-9; one-pager.md:20-22);
   - the 57 % / 99,197 of 172,579 shelf counts (HACKATHON.md:175-177; one-pager.md:29-30);
   - 96.5 % and 98.3 % / 171,108 of 174,087 (HACKATHON.md:250-256);
   - "hk.DataSources: 37 rows" (:269);
   - "all nine matched" on the Evidence page (:66).

   The committed snapshot is the exception: its lever 3 ties to E3 (1,369 + 212 − 254 = 1,327).
2. **High.** E20 low and mid (`fs:337-346`): the SQL is committed and readable, but the data and the per-approval breakdown are not. The breakdown is needed to know how much of B0 is dead-stock clearance.
3. **Medium.** The 11 % holdout share comes from the 31 Oct exports (`fs:329`), which are not in the repository. The arithmetic checks: 23,633 / 220,472 = 10.7 %.
4. **Medium.** E18 ($393.76) comes from session transcripts that are not in the repository (HACKATHON.md:235). The logged responses #19, #24 and #38 (HACKATHON.md:97-106) are in the database only.
5. **Medium.** The kickoff rulings (replica read allowed, no API key) are recorded only by the team (HACKATHON.md:242-244; commit `8544f43`).
6. **Medium.** The Works 4 justification, "the failure cases … pass" (`fs:381`), and the test counts (967 passed; 692 at import; HACKATHON.md:56, :62, :278).
7. **Low.** "Alpha's weekly pass #2 changed 8 rules" and "Nova … held" (one-pager.md:32-35).
8. **Low.** The import is "unchanged" from `b184ff55…` (DECLARATION.md:18-19). The source repository isn't available to diff against.

## Claims contradicted

9. **High. "B claims no past euro" vs the declaration.**
   - `fs:4`, HACKATHON.md:129-131 and one-pager.md:55-57 claim the calculator's measured rate as framing B's core.
   - DECLARATION.md:14-16: that effect "is **not claimed** as hackathon benefit". The calculator is declared "no AI model in it" (:9).
10. **High. "B0 is measured on the real shop, not modelled"** (HACKATHON.md:116-117; one-pager.md:42-43).
    - The sheet's own evidence rows contradict the attribution: August is "not separable from restocking" (`fs:936-938`), and September's controlled run is €3,263–4,088 against €7,461 before/after (`fs:860`).
    - Turning a before/after difference into "the calculator's effect" is itself a model.
11. **High. The headline uses a rate the team's own method text rejects.** numbers.json:501 (E20 method) says the year-over-year check does not support the uncontrolled cases. The headline is one of them.
12. **Medium. "The same sales lines reproduce the tool's own 30-day profit … most to the euro (E26)"** (HACKATHON.md:119-120; one-pager.md:46-47).
    - `fs:908` shows 3 of 8 approvals exact, 5 more off by €5–€1,018 (#37 off by 2 %).
    - More important: a data tie-out validates the export, not causality. Offered as support for B0, it's a non sequitur.
13. **Medium. "Irregularly and only in KS. Without v2 nothing secures that profit"** (kickoff-plan.md:44; `rules:30`) vs:
    - four October approvals (`fs:908`);
    - MK/AL pushes in July, September and on 21 Oct on all four layers (backtest-results.md:43-44).
14. **Medium. Gjirafa50's "Aug €2,517 / Sep €2,288"** (HACKATHON.md:117; one-pager.md:44-45) is presented as measured in those months. The SQL extrapolates #1's July-window lift across them (`e20-test-phase.sql:30`).
15. **Medium. The person's time** is "3.9 attended hours" plus 1 assumed hour (`fs:159-168`). But hours.md:25, :54 and :57 record the user giving goals and making the framing decisions inside "unattended" blocks across about 18 clock hours. The money effect is small (≈ €400–700).
16. **Low. HACKATHON.md:274 "38 unattended blocks"** vs E16 = 39 (numbers.json:448; `fs:828`); hours.md now has 40 such rows. The team's "fixed" in dry-run-3.md:50 ("E16 38 blocks") was already stale.

## Rule compliance

17. **Pass, with a note (medium). Timing.**
    - Every commit after the import is dated 16:50 on 30 Oct or later. Dates are self-reported, so they prove nothing either way.
    - Commit 1 (`b3f19a6`) holds 1,237 lines prepared before the kickoff. Besides what HACKATHON.md:35-37 lists, it also contains `docs/Dynamic Discounts Platform integration.docx` and `CLAUDE.md`, which that list leaves out. I couldn't open the docx.
    - The organisers rule on commit 1.
18. **High. Declared vs new.** The import is cleanly labelled (`9c977ca`), and the new/changed file counts check: 124 / 5 / 21 / 21 / 3 / 31 new, 20 changed. But the money claim is mostly the declared code's effect (finding 9).
19. **Pass. Secrets.**
    - `git log --all -p -G` for `sk-ant|AccountKey=|PRIVATE KEY|Password=|pwd=|User ID=|Uid=|Bearer|api_key=` finds only documents describing the check.
    - Every tracked connection string points at `(localdb)`.
    - No CSV or backup file was ever committed.
20. **Low. Personal data.** An employee's work email is still in the import commit `9c977ca` (disclosed, HACKATHON.md:270). The draft xlsx in commit 1 carries the Office user's name (hours.md:36). Sales lines carry no customer fields (by the team's account; not checked against data).
21. **Medium. Live systems.**
    - The team's own summary says nothing connects to the NOP replica during the build (`rules:7`).
    - In practice the engine runs on the replica (hours.md:10), the cost watcher polls it every 15 minutes, and a daily mirror reads it (HACKATHON.md:200-202).
    - This rests on the team-recorded ruling. The dev-database write is disclosed (HACKATHON.md:271).
22. **Medium. Every number labelled.** Mostly yes; E21's label is now fixed. Still open:
    - The score bands, labelled "from the hackathon rules", are untraceable beyond net benefit 4 and 5.
    - Every cost and ramp input is "assumed" with no basis (finding 29).
23. **Pass. Mocks shown.** Publishing is sandbox-only (all three configs have `"Target": "Sandbox"`), injected tests live in a separate database, and HACKATHON.md:195-262 lists what isn't finished. I couldn't see the UI banner.
24. **Pass. AI decides rather than only explains,** structurally (findings 32-35). But see finding 37 on where the money comes from.

## Financial sheet

25. **Pass. The arithmetic checks out:**
    - one-time (3.9 + 1 + 160 + 8) × 50 + 393.76 = 9,038.76 ✓
    - running 12 × (68.39 + 400) + 52 × 25 = 6,920.66 ✓
    - B·Mid (4,158 + 10,409) × 0.89 = 12,964.63 ✓; net 107,204.57 ✓; ROI 6.72 ✓
    - payback 3 + 4,286.6 / 12,387.9 = 3.35 ✓
    - lever total 1,996 / 4,158 / 8,675 = E8 ✓ (`fs:322`)
26. **High. Existing-system money counted as new benefit.**
    - B0 is earned today by the declared calculator through manual pushes, which carried on through October.
    - The sheet counts all of it from month 2, including the build month's successor months, as v2's benefit (`fs:442-443`).
    - Within the sheet there is no arithmetic double count between B0 and levers 1, 3, 5 and 8. B0 is realised GP net of leakage; lever 8 adds the leakage back; lever 5 scores pre-approval days. The double count is against the baseline.
27. **High. The net-benefit 5 is fragile** (the Scores section): headroom 6 %; lever 3 on September alone, or one more pilot month, gives rating 4.
28. **Medium. B0's construction.**
    - Each approval's 30-day step is measured over the *previous* approval's prices: #25's OldPrice is the post-#12 price. These steps are summed into a steady monthly "rate".
    - That is neither the level effect against no calculator nor a measured monthly flow. Gjirafa50 is extrapolated from July. Dead-stock clearance sets are included without a breakdown.
29. **Medium. Inputs without a source:**
    - 1 h of the person's time;
    - 160 h to production, 8 h training, 8 h a month maintenance, 0.5 h a week review;
    - servers €0 (assumes a headless Claude Code with a subscription OAuth token on k8s, neither built nor costed; `fs:194`);
    - pilot start, length and the 25 % share (`fs:205-217`);
    - every call volume (`fs:389-422`);
    - the elasticities;
    - $1 = €1.
30. **Medium. Lever 4.**
    - B·Mid values it at E20 mid's rate (`fs:282`).
    - Its "MK/AL months without a push" counterfactual is shrinking: all four layers were pushed on 21 Oct.
    - Lever 8 high (€2,830) is October's anomaly, caused by the team stacking its own push sets, carried for 12 months (`fs:310`).
31. **Pass. No revenue counted as margin** (every gain is GP at booked COGS) **and no hours saved counted as money** (`fs:141`; the time sheet exports no rows).

## AI decides / code enforces

32. **Pass. Alpha → floors and targets.**
    - The model's JSON goes through `SafetyLimits.Evaluate` (FloorTargetAgent.cs:149-154). Its checks (SafetyLimits.cs:42-81): holdout and thin rows kept; malformed, out-of-range, target-below-floor and hard-minimum violations rejected; paused rows and steps over 1 point a week held.
    - Only applied non-keeps are written (FloorTargetAgent.cs:167).
    - Approval re-checks the hard minimum, target ≥ floor, holdout and staleness (:205-222).
    - Approving a paused hold applies it. The doc comment now says so (:186-191); the behaviour is unchanged.
33. **Pass. Run checker: the only place a model number becomes a price directly.**
    - `fix_price` is accepted only if every rule re-checks at Info or below (RunChecker.cs:131-138, RunCheckRules.cs:57-104), then exported (CheckedExport.cs:120).
    - The model can't downgrade a block (RunChecker.cs:142-144).
    - **Low:** in the dead-stock lane, a model fix may sit below cost, down to 40 % of cost (RunCheckRules.cs:71-74). It is bounded but below cost.
34. **Pass. Campaign planning.**
    - Code computes every price, bounded at cost / (1 − 3 %) and under today's price (CampaignPlanner.cs:103-111).
    - **Low:** the 3 % is configuration with no lower bound (CampaignLimits.cs:27). This is disclosed.
35. **Pass. Publisher.**
    - In sandbox mode, a schema outside `shop_sandbox_*` throws (PublisherOptions.cs `ShopFor`).
    - A raise above max(anchor, current), or one without a ceiling, is refused again in the writer (NopRegularPriceWriter.cs:41-46).
    - **Low:** the guard checks the schema name, not the server (disclosed).
36. **Medium. A chat goal is the model's transcription, stored as the person's binding goal:** `GoalValue = b.GoalValue` (AgentChat.cs:228) vs `GoalSetBy = <person>` (:261). This is disclosed.
37. **Medium. The euros barely depend on the AI's decisions.**
    - Alpha's floors: €2 a month (E2).
    - Lever 7 is code's per-SKU depth; the model picks among four strategies code computed.
    - Under framing B, about 0 % of B0 and most of the levers are non-AI.
    - The AI is real and fenced in, but it isn't what earns the money.

## Other findings

38. **Medium. The headline moved up after the reviewer flagged it, with no new evidence.**
    - At `d980294`, numbers.json's `sinceRegistration` said "the claim narrowed to what a control supports" and led with €4,402.
    - After dry run 3 called the uncontrolled rate unsupported, Prompt 13 ("just 10k month make a strong case for it because we have it", `prompts/13-b-headline-10k.md:3`) made €10,409 the headline and relabelled €4,402 "a floor" (numbers.json:504).
    - The new arguments (scripted, live rerun, E26 tie-out, substitution bias) concern reproducibility, not causality.
39. **Low.** The Evidence page compares a live rerun of each SQL with the value the same SQL stored. That shows reproducibility, not validity.
40. **Low.** `pass.Applied` still counts keeps (FloorTargetAgent.cs:169).
41. **Low.** The headline changed at 10:32, 4 minutes after the last fixes and 20 minutes after the previous review.

---

## Dry run 3 findings

| # | Sev. | Status | Evidence |
|---|---|---|---|
| 1 | high | Not resolved (inherent) | DB still not supplied; finding 1 above |
| 2 | high | **Resolved** | €13,000 out of the claim: `fs:12` "not claimed", numbers.json:499; test asserts it (EvidenceTests.cs diff) |
| 3 | med | Disclosed only | Controls still chosen, one run per layer (`fs:340`) |
| 4 | med | Not resolved (inherent) | E22 needs the DB |
| 5 | med | Disclosed only | HACKATHON.md:235 |
| 6 | med | Disclosed only | HACKATHON.md:242-244 |
| 7 | low | Not resolved (inherent) | — |
| 8 | low | Not resolved (inherent) | — |
| 9 | high | **Not resolved** | DECLARATION.md:14-16 unchanged; B0 is now the explicit headline |
| 10 | high | **Not resolved, now more prominent** | numbers.json:501 still rejects the uncontrolled cases; headline uses one |
| 11 | high | Partly | The wording now counts E23/E5/E4 once, as levers (HACKATHON.md:131-133). That leaves no stated reason B0 itself is incremental (finding 26) |
| 12 | med | Partly | one-pager.md:8-9 fixed; kickoff-plan.md:44 still says "only in KS… nothing secures that profit" |
| 13 | med | **Resolved** | one-pager.md:26-27; HACKATHON.md:203-205 |
| 14 | med | **Resolved** | one-pager.md:22-23 "built… tested; no pushed change has completed its window" |
| 15 | low | Partly | 170 commits, 310 files / 74,237 insertions and the per-project counts are verified correct at `d980294`; E15 53 / $7.90 ✓. HACKATHON.md:274 says 38 blocks vs E16 39 |
| 16 | low | **Resolved** | HACKATHON.md:84 matches RunChecker.cs:131-144 |
| 17 | low | **Resolved** (comment only) | FloorTargetAgent.cs:186-191; behaviour unchanged and now described |
| 18 | med | Not resolved (organisers' call) | Commit 1 unchanged; its docx isn't listed |
| 19 | high | **Not resolved** | Same as 9 |
| 20 | pass | Pass | My history scan |
| 21 | low | Disclosed only | Email in `9c977ca`; xlsx metadata in `b3f19a6` |
| 22 | med | Disclosed only | `rules:7` vs HACKATHON.md:200-202 |
| 23 | med | Partly | E21 relabelled "estimated" (numbers.json diff); score bands and cost inputs still unsourced |
| 24 | pass | Pass | — |
| 25 | high | Disclosed only | Snapshot unchanged (2,554 / 184); HACKATHON.md:237-239 |
| 26 | high | **Not resolved** | Finding 26 |
| 27 | med | Disclosed only | HACKATHON.md:164-166 |
| 28 | med | Not resolved (kept) | `fs:303`, `fs:310` |
| 29 | med | Not resolved (kept) | `fs:159-217` |
| 30 | med | Not resolved (kept) | Ramp unchanged (`fs:205-221`) |
| 31-34 | pass | Pass | Re-verified (findings 32-35) |
| 35 | med | Disclosed only | AgentChat.cs:228, :261 unchanged |
| 36 | med | Disclosed only | HACKATHON.md:170-181 |
| 37 | low | Not resolved, repeated | Headline changed at 10:32 (finding 41) |
| 38 | low | **Resolved** | HACKATHON.md:175 names the runs |
| 39 | low | Not resolved (kept) | FloorTargetAgent.cs:169 |
| Biggest assumption (B0 causal and lost without v2) | high | **Not resolved** | October approvals and MK/AL pushes continue (`fs:908`; backtest-results.md:43-44) |

**Summary:**
- **Resolved:** 7 (#2, #13, #14, #16, #17, #38, plus the pass rows).
- **Partly:** 4 (#11, #12, #15, #23).
- **Disclosed only:** 10.
- **Not resolved:** the rest, including every high finding about framing B (#9, #10, #19, #26 and the biggest assumption).

Since dry run 3, the code changes are one doc comment and the Evidence page's list. Everything else is wording, plus the headline moving to the uncontrolled rate.