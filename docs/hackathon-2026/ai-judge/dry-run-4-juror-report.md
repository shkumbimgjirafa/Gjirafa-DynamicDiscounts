# Juror report: Pricing Tool v2 (Track A, Money), HEAD `e86aeff`

**What I could and couldn't do.** I read everything listed in the brief, the code paths cited below, and the git history: 200 commits after the import, 197 of them up to `3116cab`. I could not run the app, the tests or the SQL, and the hackathon database isn't supplied. Scoring bands I can trace to the rules summary: net benefit 4 and 5 (`rules-summary.md:16`) and the Evidence and Works anchors (`:19-20`). Every other band (net benefit 1–3, all ROI and payback bands) comes from the team's own "Score bands" tab (`fs:615-643`), which calls itself "from the hackathon rules". `fs:` means the sheet dump.

**Costs I used everywhere**, all checked against the sheet:
- one-time €9,172.40;
- running €6,927.26 a year (€577.27 a month);
- total €16,099.66;
- 9.5 full-benefit months;
- 0.89 holdout factor.

---

## 1. Scores

| Row (weight) | Team's sheet | **B at the claim** (B0 €10,409, granted for argument) | **B at the controlled measurement** (B0 €4,402) | **A, cross-check** |
|---|---|---|---|---|
| Net benefit (40) | 5 | **4** | **4** | **2** |
| ROI (10) | 5 | **5** | **5** | **3** |
| Payback (10) | 3 | **3** | **3** | **2** |
| Evidence (20) | 3 | **2** | **2** | **2** |
| Works (20) | 4 | **2** (3 if Run week runs live) | **2** (3) | **2** (3) |
| **Total** | **84** | **64 (72)** | **64 (72)** | **42 (46)** |

**B at the claim** (€12,965 a month)
- **Net benefit 4.** The sheet's €107,064 clears the €100k line by only 6 % (it needs €12,221 a month, `fs:52`). Lever 3 depends on one August cost event: GjirafaMall made €2,554 in August and €184 in September (`backtest-snapshot.json:647, :674`). With September alone the month is about €11,956 and net benefit about €97.5k.
- **ROI 5.** 665 % (`fs:17`) is far above every band, and stays there on any B0 I would accept.
- **Payback 3.** 3.36 months (`fs:23`), but it comes from an assumed ramp: build in month 1, then a 2-month pilot at 25 % (`fs:121-136`).
- **Evidence 2.** The headline input is contradicted by the team's own controls (E20 low, E27). Nothing can be rerun without the database. Nothing was measured live; the money ledger is empty.
- **Works 2.** Nothing could be run. It moves to 3 only if a full Run week on real data is shown live. It can't reach 4:
  - the Për ty reproduction test fails (E11);
  - the ledger is empty;
  - the start-up task is disabled;
  - nothing has been published to the shop;
  - the demo's cost-injection step can't show what it promises (§3).

**B at the controlled measurement**
- **Net benefit 4.** I kept the levers at their central case, with lever 4 at its controlled rate (€560), so the levers come to €3,393. (4,402 + 3,393) × 0.89 = €6,938 a month, net about €49.8k.
- That 4 is fragile; three changes from the team's own data each drop it to 3:
  - B0 at E27's sold-in-both-windows figure, €2,749: net about €35.8k;
  - the levers at elasticity −2 (the team's B·Low): €38.0k (`fs:410`);
  - lever 3 on September alone: about €40.2k, which only just keeps the 4.
- **ROI 5.** About 309 %.
- **Payback 3.** About 4.2 months on the sheet's ramp.
- **Evidence 2.** It is the better-supported number: it agrees with E27's September figures (€3.9–4.3k for GjirafaMall). But it is one run per layer, Gjirafa50's control comes from a July run, and I couldn't rerun it.
- **Works 2 (3).** As above.

**Framing A** (levers only, lever 4 at €560)
- **Net benefit 2.** €3,393 × 0.89 = €3,020 a month, net about €12.6k. With lever 3 on September alone, net is about €3.0k (rating 1).
- **ROI 3.** About 78 %.
- **Payback 2.** About 6.9 months.
- **Evidence 2.** The levers come from walk-forward backtests on past data. That would be a 3 if I could rerun them; the elasticities are assumed.
- **Works 2 (3).** As above.

### Do I accept €10,409 a month as the rate? **No.** A defensible rate from this repository is about €2.7–4.4k a month.

**1. B0, broken down.** I rebuilt it from E20's own per-approval lines (`fs:828`; numbers.json E20 at `:471`). Each contributor's monthly average:

| Contributor | € a month | Share | Problem |
|---|---|---|---|
| Approval #12 (GjirafaMall, 6 Aug) | ≈ 3,934 | 38 % | Confounded by restocking. Only 45 of the 1,244 repriced SKUs sold in both windows (E27, `fs:884`), and the sheet calls it "not separable from restocking" (`fs:920-922`) |
| Approval #25 (GjirafaMall, 3 Sep) | ≈ 2,884 | 28 % | Its own controlled run gives €3,263–4,088 a month against €5,768 before/after |
| Approval #1 (Gjirafa50, 21 Jul) | ≈ 1,617 | 16 % | A July lift carried across August and September |
| Dead-stock sets #6, #7, #13, #19, #22 | ≈ 1,423 | 14 % | One-off inventory clearance, not a recurring margin gain |
| Approvals #2 and #4 | ≈ 560 | 5 % | — |

**2. The new robustness check cuts B0 to about a quarter.** Dry run 3b asked for a restock-robust control. The team built it (E27, commit `fc57ff4`) and it gives **€2,749 a month** (GjirafaMall €2,611, Gjirafa50 €138; numbers.json `:1005-1016`, `fs:884`). The documents still cite "robustness checks: E20, E27" for €10,409 without the figure (`one-pager.md:49-50`, `HACKATHON.md:19`, `:127-128`, `kickoff-plan.md:719`).

The team's own Prompt 15 asked for this sentence: "it is €2.7k when only products that sold in both windows count", "so no reader finds a number we left out" (`prompts/15-test-ends-without-the-build.md:64-68`). It is not in the one-pager or in HACKATHON.md.

**3. E20's own method text still rejects the uncontrolled cases.** It says the year-over-year check "does not support the uncontrolled two" (numbers.json `:507`, `fs:831`).

**What would change my mind**, all of it in the repository:
- E27 rerun on SKUs **in stock** in both windows, from stock snapshots, rather than SKUs that **sold** in both. A price rise can stop sales, so "sold in both" may cut too much. It would have to come back near €10k.
- Same-weeks controlled runs for the October approvals #30, #32, #35 and #37, plus one Gjirafa50 run inside August–September, adding up to about €10k a month.
- The pilot's holdout difference-in-differences from the money ledger.
- To count B0 as *new* benefit at all: evidence that the manual pushes are stopping. E28 shows the opposite. GjirafaMall KS ran on 6 and 19 Oct, Gjirafa50 KS on 7 and 21 Oct, and all four MK/AL layers got a full push on 21 Oct (`fs:892`).

## 2. The single biggest unproven assumption

**That €10,409 a month is the calculator's effect *and* disappears without this build** ("Without the build, the test ends", labelled assumed, `fs:281-283`).
- It carries 71 % of the claim (€9,264 of €12,965), and lever 4 borrows the same rate.
- The causal half is cut to €2.7–4.4k by the team's own E20 low and E27.
- The incremental half is contradicted by E28: the manual process sped up in October.
- If either half fails, framing B collapses toward framing A's €12.6k net.

## 3. Claims I can't verify, and claims contradicted

### Not verifiable from this material
1. Every runtime figure, because `PricingToolHackathon` isn't supplied:
   - the cost watcher's 293 moves, 54 holds and 0.7–9.1 minutes (`one-pager.md:20-22`, `HACKATHON.md:7-9`, `kickoff-plan.md:711`);
   - 57 %, 909 raises, 96.5 % and 1.5 % (`one-pager.md:31-32`);
   - 49,214 of 50,969 and 25,390 of 26,604 prices on the sandbox shelf (`one-pager.md:33-34`, `HACKATHON.md:282-284`);
   - 171,108 of 174,087 (`HACKATHON.md:278-280`);
   - engine runs #1100 and #1101 and their SKU counts (`one-pager.md:36-37`);
   - Alpha's pass #2 changing 8 rules, and Nova's held plan (`one-pager.md:37-40`, `kickoff-plan.md:694`);
   - "hk.DataSources: 40 rows" (`HACKATHON.md:297`);
   - logged responses #19, #24 and #38 (`HACKATHON.md:106-115`).
2. "45 of 45 checks match" (`evidence.html:24`, `HACKATHON.md:75`, `kickoff-plan.md:715`). The file is self-generated, and it shows reproducibility, not validity.
3. The tests: 1,012 passed and 692 at import (`HACKATHON.md:62`, `:68`, `:307`, `fs:804`); "the failure cases … pass" (`fs:301`).
4. E18, $527.40 of build-session AI usage: the transcripts aren't in the repository (`HACKATHON.md:259`).
5. The holdout share of 0.11 comes from the 31 Oct exports, which aren't committed (`fs:244`). Its arithmetic checks (10.7 %).
6. The kickoff rulings (replica read allowed, tool database readable, no API key) are recorded only by the team (`8544f43`, `ffc3af2`, `HACKATHON.md:266-268`).
7. "Imported unchanged" (`DECLARATION.md:18-19`): the source repository isn't available to diff against.
8. "No marketplace commission applies" and "returns not netted" (`one-pager.md:75-77`).
9. "Run week … shown live on Monday" (`one-pager.md:51-53`) is a future event. The demo script presses Run week 10 minutes *before* the demo (`kickoff-plan.md:684`), so the live part is the read-out, not the run.
10. "The worked example in the hackathon rules scores 76" (`fs:40`).

### Contradicted
11. **High. The B0 headline vs its own robustness checks.** `one-pager.md:48-50` and `HACKATHON.md:19`, `:126-128` present €10,409 with "robustness checks: E20, E27". E20 low gives €4,402 and E27 restock-robust gives €2,749 (`fs:828`, `fs:884`), and E20's method text says it is unsupported (numbers.json `:507`).
12. **High. "Not claimed" vs the claim.** `DECLARATION.md:14-16` says the calculator's measured effect "is **not claimed** as hackathon benefit"; it is 71 % of the claim (`one-pager.md:65`). Saying "B claims no past euro" (`fs:4`) relabels the issue; it doesn't remove it.
13. **High. The without-the-build line vs E28.** `one-pager.md:55-61` and `kickoff-plan.md:45` ("only in KS. Without v2 nothing secures that profit") vs E28: four KS runs in October and full pushes on four MK/AL layers on 21 Oct (`fs:892`). Line 45 is still there under the dated note at `:44`.
14. **Medium. Stale headline figures.** `HACKATHON.md:123` says net benefit €107,205, ROI 672 %, payback 3.3 months. The sheet (`fs:5`) and `one-pager.md:46` say €107,064, 665 % and 3.4.
15. **Medium. Stale AI usage figures.** `HACKATHON.md:99-100` gives 53 calls and $7.90 (E15), and $393.76 / $298.03 (E18). E15 is now 85 calls and $10.29 (`fs:788`; `HACKATHON.md:290`; `kickoff-plan.md:713`). E18 is $527.40 / $345.49 (`fs:812`; `HACKATHON.md:303`; `one-pager.md:80`). The same file contradicts itself.
16. **Medium. "Rules only tighten"** (`HACKATHON.md:94`) vs the code:
    - `protect-discount` stops the publisher and the cost watcher from ending a team discount "even under the floor" (`AgentChat.cs:476`, `CostWatcher.cs:84`);
    - its value can be `*`, which matches every discount (`ChatRecords.cs:69-71`); code only checks that it isn't empty (`AgentChat.cs:455`);
    - `margin-target` raises shelf prices (`MarginTarget.cs:93-115`).
17. **Medium. "Depth cap 25 %"** (`HACKATHON.md:91`) vs a boolean the model returns. `NoDepthCap` lifts the cap to 90 % (`AgentChat.cs:374-377`, `:400`, `:538`; `CampaignLimits.cs:26`, `:61`). It is disclosed later as "a person's terms" (`HACKATHON.md:237-239`), but section 3's table still states the cap.
18. **Medium. The demo's safety step can't work as written.** The script runs `cost-watch --inject … --test`, and "the Chat tab shows the raise" (`kickoff-plan.md:710-711`). `--test` sends the agents' records to `PricingToolHackathonTest` (`AgentTools/Program.cs:88-96`), and `PricingTool.Web` never reads the test database (no reference to it anywhere in Web).
19. **Medium. The person's time.** It is costed at 3.9 attended hours plus 1 assumed hour (`fs:74-83`). Every attended row in `hours.md` ends by Fri 19:58, yet Saturday's commits record the user's decisions: `be566c1`, `def8b85`, the headline call in `a9d135d`, and Prompt 15. The cost effect is small (about €400–700), but the rule says every person-hour costs €50.
20. **Low. "Exceptions inbox"** (`DECLARATION.md:31`) was replaced by the Chat tab's "Waiting for you" (`kickoff-plan.md:681-682`). "Go-live ready" (`DECLARATION.md:33`) vs the go-live prerequisites still open: a DBA grant, the shop's answers on its cache and `OldPrice`, and the owner's call (`HACKATHON.md:229-231`).

## 4. Rule compliance

| Rule | Finding |
|---|---|
| Timing, built vs before | **Pass, with a note.** The import `9c977ca` is labelled. All 200 later commits are dated 30–31 Oct after 16:50, and author and committer dates agree. Dates are self-reported. Commit 1 (`b3f19a6`, 16:50) holds 1,237 lines prepared beforehand: the plan, CLAUDE.md, a draft xlsx, a docx and a generator. Now fully listed at `HACKATHON.md:39-43`; the organisers rule on it. The file counts check: 149 / 5 / 22 / 32 / 3 new, 30 changed, 36 tests; 373 files and 97,150 insertions at `3116cab`. |
| Data access | **Depends on a ruling only the team recorded.** The rules summary says "nothing connects to … the NOP replica during the build" (`rules-summary.md:7`, `kickoff-plan.md:20`). The code reads the replica: the cost watcher every 15 minutes and the daily mirror (`HACKATHON.md:214-219`). That rests on the ruling recorded in `8544f43`, 2 minutes after commit 1. The connection lives only in user-secrets. The write to a local dev database is disclosed (`HACKATHON.md:299`). |
| Shop writes | **Pass, by code.** Every config sets `"Target": "Sandbox"` (`Web/appsettings.Hackathon.json:39`, `Engine/…:30`, `AgentTools/appsettings.json:29`). The writer throws for any schema outside `shop_sandbox_*` (`PublisherOptions.cs:61`). It checks the schema name, not the server (disclosed). |
| Secrets | **Pass.** `git grep` on HEAD and `git log --all -p -G` for key, password and private-key patterns turn up only documents that describe the check. No `.env`, `k8s/`, CSV or backup is tracked. |
| Personal data | **Low.** HEAD is clean (only a `name@gjirafa.com` placeholder, `Views/Users/Index.cshtml:18`). The import commit still has an employee email in 6 files (disclosed, `HACKATHON.md:298`). I couldn't check the sales data for customer fields. |
| Labelling of numbers | **Partly.** numbers.json labels every figure. Not labelled or not sourced: the score bands (`fs:615`), "worked example 76" (`fs:40`), and the cost and ramp inputs (§5). "Robustness checks" is the wrong label for two checks that cut B0 by 58–74 %. |
| Mocks shown | **Pass on paper.** Injected tests are tagged and kept in a separate database (`HACKATHON.md:220-221`), and the unfinished parts are listed (`:222-247`). I couldn't see the UI banner. |
| Others writing the financial case | **Note for the organisers.** An organiser's feedback shaped the financial presentation: "Mergca's split benefit table", "Mergca flagged" (`prompts/15…:46`, `:69`). That is probably guidance rather than authorship, but it is a rule the summary names (`rules-summary.md:11`). |

## 5. The financial sheet

- **Arithmetic: pass.**
  - One-time: (3.9 + 1 + 160 + 8) × 50 + 527.40 = 9,172.40 ✓ (`fs:383`).
  - Running: 12 × (68.94 + 0 + 400) + 52 × 0.5 × 50 = 6,927.26 ✓ (`fs:391`).
  - Monthly: (4,158 + 10,409) × 0.89 = 12,964.63 ✓, × 9.5 = 123,164 ✓, net 107,064 ✓, ROI 6.65 ✓.
  - Payback: 3 + 4,421.9 / 12,387.4 = 3.357 ✓ (`fs:612`).
  - The lever total (`fs:237`) and the benefit-by-source check (`fs:477`) both match.
- **Track A formula: followed.** Net = year-1 benefit − all costs, ROI = net ÷ cost, payback from a monthly grid.
- **Revenue counted as margin: no.** Every benefit is gross profit at booked COGS. **Hours saved counted as money: no** (`fs:481-483`).
- **Double counting.** I found no arithmetic double count inside the sheet: the levers are scored on top of the calculator era's actual prices, and lever 8 adds leakage back after levers 1–6. The double count is against the baseline: B0 is money the declared calculator earns by hand today, and it kept doing so in October, yet all of it is counted as v2's from month 2 (`fs:445-449`).
- **One-off gains counted as a monthly rate.**
  - About €1.4k a month of B0 is dead-stock clearance.
  - About €3.9k a month is #12's restocking.
  - Lever 3's €1,327 is mostly one August cost event.
  - Lever 8's high case is October's overlapping push sets, carried for 12 months (`fs:225`).
- **Inputs without a source** (all "assumed"):
  - 1 hour of the person's time inside unattended blocks;
  - 160 hours to production, 8 hours training, 8 hours a month maintenance, 0.5 hours a week review;
  - servers €0, which assumes a subscription OAuth token running in production k8s, neither built nor costed (`fs:105-109`);
  - setup €0;
  - pilot start, length and the 25 % share;
  - every call volume (`fs:309-342`);
  - the elasticities;
  - $1 = €1.
  - The holdout 0.11 rests on exports that aren't committed.
- **Self-ratings.** Evidence 3 and Works 4 (`fs:290-301`) are the team's own. They add 8 points of the 84 over my ratings.

## 6. "The AI decides, code enforces": three spot-checks

1. **Alpha's floors and targets.** Passes.
   - The model's JSON goes through `SafetyLimits.Evaluate` (`FloorTargetAgent.cs:150-155`). It keeps holdout and thin-data rows; rejects malformed, out-of-range, target-below-floor and below-hard-minimum values; and holds paused rows and steps over 1 point a week (`SafetyLimits.cs:42-81`).
   - Only applied, non-keep decisions are written (`FloorTargetAgent.cs:168`).
   - Approving a held decision re-checks the hard minimum, target ≥ floor, holdout and staleness (`:206-223`).
   - Low: `pass.Applied` still counts keeps (`:170`).
2. **Run checker `fix_price`** (a model number that becomes a price). Passes.
   - A fix counts only if every rule re-checks at Info or below (`RunChecker.cs:131-138`, `RunCheckRules.cs:57-104`).
   - The model can't downgrade a block, and an escalation becomes a block (`:142-144`).
   - Low: in the dead-stock lane a fix may go down to 40 % of cost (`RunCheckRules.cs:73-74`).
3. **Campaigns from the chat.** **Code checks the prices, but model output can switch the checks.**
   - Every campaign price is bounded at cost ÷ (1 − 3 %) and below today's price (`CampaignPlanner.cs:114-120`). The depth cap is clamped in code (`CampaignLimits.cs:61-62`).
   - But three switches come straight from booleans in the model's chat JSON, recorded as the person's: `NoDepthCap` (`AgentChat.cs:400`, `:538`), `EverySkuInStock`, and `DiscountOptimizationOff` (`:401`, `:539`).
   - No code parses the person's message to confirm any of them.
   - `NoDepthCap` raises the cap from 25 % to 90 % (`CampaignPlanning.cs:114`).
   - `DiscountOptimizationOff` switches off an engine setting on the campaign's days (`CampaignSwitches.cs:41-48`, audited).
   - The same applies to the binding goal (`AgentChat.cs:397`, `:439`) and to `protect-discount` and `margin-target` rule values (`:451-466`). These are bounded by the 3 % floor, the regular-price ceiling and the 0–100 range, but they are not "a person's" values in any sense code can check.

## 7. Dry run 3b's findings now

| # | Status now | Evidence |
|---|---|---|
| 1 DB not supplied | Not resolved | — |
| 2 E20 breakdown | **Partly**: per-approval split committed | `fs:828`; data still absent |
| 3 holdout 11 % source | Not resolved | `fs:244` |
| 4 E18 transcripts | Not resolved (figure now $527.40) | `HACKATHON.md:259` |
| 5 kickoff rulings | Not resolved | `8544f43`, `ffc3af2` |
| 6 test counts, Works reason | Not resolved | `fs:301`, `fs:804` |
| 7 Alpha pass #2, Nova held | Not resolved | — |
| 8 import "unchanged" | Not resolved | — |
| 9 B uses declared money | **Not resolved** | DECLARATION.md unchanged since `b3f19a6`; reframed at `fs:4` |
| 10 "measured, not modelled" | Wording fixed; **substance worse** | E27 restock-robust €2,749 |
| 11 method text rejects headline | **Not resolved** | numbers.json `:507` |
| 12 E26 tie-out wording | **Resolved** | `HACKATHON.md:131-132` |
| 13 "only in KS" | **Partly** | note at `kickoff-plan.md:44`; `:45` unchanged |
| 14 Gjirafa50 Aug/Sep wording | **Resolved** | `one-pager.md:50-51` |
| 15 person's time | Not resolved | §3 item 19 |
| 16 "38 blocks" | **Resolved**, but new stale figures | `HACKATHON.md:99-100`, `:123` |
| 17 commit 1 list | **Resolved** (organisers rule) | `HACKATHON.md:39-43` |
| 18 declared vs new | Counts pass again; money issue = #9 | — |
| 19 secrets | Pass (re-verified) | — |
| 20 personal data | Disclosed, unchanged | `9c977ca` |
| 21 live systems | Disclosed, unchanged | — |
| 22 labels | **Partly** | score bands and "76" still unsourced |
| 23 mocks | Pass (UI unseen) | — |
| 24 AI decides | Pass, with new caveats | §6 item 3 |
| 25 arithmetic | Pass with the new costs | §5 |
| 26 existing money as new | **Not resolved**; now labelled assumed and contradicted by E28 | `fs:283`, `fs:892` |
| 27 fragile 5 | Not resolved (6 % headroom) | `fs:52` |
| 28 B0 construction | Disclosed only | `fs:828` |
| 29 unsourced inputs | Not resolved | §5 |
| 30 lever 4 / lever 8 high | Not resolved | `fs:197`, `fs:225` |
| 31 revenue and hours | Pass | — |
| 32–35 code paths | Pass (re-verified) | §6 |
| 36 chat goal transcription | **Not resolved, and wider**: the depth cap, discount optimization and protect rules also come from the model | `AgentChat.cs:400-401`, `:538-539` |
| 37 euros barely depend on AI | Not resolved (E2 = €2) | `one-pager.md:74` |
| 38 headline moved without evidence | **Not resolved, worse**: the new evidence (E27) went against it and is left out of the documents' text | §1 |
| 39 reproducibility vs validity | Not resolved | `evidence.html:24` |
| 40 `pass.Applied` counts keeps | Not resolved | `FloorTargetAgent.cs:170` |
| 41 headline timing | Unchanged | — |
| Biggest assumption | **Not resolved** | §2 |
| "What would change my mind" | Per-approval split ✓; restock-robust control ✓ (**€2,749**); October controls ✗; ledger ✗; pushes stopping ✗ (they sped up) | — |

**Summary.** Since dry run 3b:
- Resolved: 4 wording findings (#12, #14, #16, #17).
- Partly: #2, #13 and #22.
- Every high finding about framing B is open, and the one new piece of causal evidence the team produced points against the headline.
- New since then: the stale figures in HACKATHON.md, a demo step that can't show its result, and chat switches set by the model that lift code limits.