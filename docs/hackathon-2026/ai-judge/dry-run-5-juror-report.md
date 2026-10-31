# Juror report: Pricing Tool v2 (Track A, Money), HEAD `ee42ba0`

## What I could and couldn't check

**Read:**
- the rules summary, `DECLARATION.md`, `HACKATHON.md`, the one-pager and `kickoff-plan.md` §7;
- the sheet dump (cited as `fs:`), `numbers.json` and `evidence.html`;
- the code paths cited below and the full git history: 203 commits, 201 after the import, of which 197 are up to `3116cab`. HACKATHON.md's "197" is correct.

**Not run:**
- the app, the SQL or the tests. The hackathon database isn't supplied, and the tests create LocalDB databases on this machine.
- a `dotnet build`. My build command needed approval, so I didn't run it.

Every runtime figure is therefore unverified.

**Scoring bands:** the rules summary only fixes net benefit 4 (€40–100k) and 5 (above €100k), plus the Evidence and Works anchors (`rules-summary.md:16-20`). Every other band comes from the team's own "Score bands" tab (`fs:615-643`), which says it is "from the hackathon rules". I can't check that.

**Costs I used** (all re-derived from the sheet):

| Item | Value |
|---|---|
| One-time | €9,204.09 |
| Running | €6,927.26 a year (€577.27 a month) |
| Total | €16,131.35 |
| Full-benefit months in year 1 | 9.5 |
| Holdout factor | 0.89 |

---

## 1. Scores

| Row (weight) | Team's sheet | **B at the claim** | **B at the controlled measurement** | **A, cross-check** |
|---|---|---|---|---|
| Net benefit (40) | 5 | **4** | **4** | **2** |
| ROI (10) | 5 | **5** | **5** | **3** |
| Payback (10) | 3 | **3** | **3** | **2** |
| Evidence (20) | 3 | **2** | **2** | **2** |
| Works (20) | 4 | **2** (3 if Run week runs live on real data) | **2** (3) | **2** (3) |
| **Total** | **84** | **64 (72)** | **64 (72)** | **42 (46)** |

### B at the claim (€12,965 a month, B0 granted for argument)

- **Net benefit 4.** The sheet's €107,033 (`fs:11`) clears €100k only because the month is €741 above the €12,224 needed (`fs:52`), about 6 % headroom.
  - Lever 3's central €1,327 is mostly one August cost event (`HACKATHON.md:263-265`).
  - On September alone (€148, `fs:187`) the month is €11,915 and net is €97.1k, which is a 4.
- **ROI 5.** 664 % (`fs:17`).
- **Payback 3.** 3.36 months (`fs:612`), from an assumed ramp: build in month 1, then a 2-month pilot at 25 % (`fs:120-136`).
- **Evidence 2.**
  - The headline input is cut by 58–74 % by the team's own controls: E20 low €4,402 and E27 €2,749 (`fs:828`, `fs:884`).
  - E20's own method text says the year-over-year check "does not support the uncontrolled two" (`numbers.json:507`).
  - Nothing can be rerun without the database, nothing was measured live, and the money ledger is empty (`HACKATHON.md:249`).
- **Works 2.** I could run nothing.
  - It rises to 3 only if a full Run week on real data is shown.
  - It can't reach 4: Për ty fails (E11), the ledger is empty, the start-up task is disabled, nothing has been published to the shop, and the demo's injection step leaks into the sandbox (§3 item C1).

### B at the controlled measurement (B0 = €4,402)

- **Net benefit 4.** Levers at their central case, with lever 4 at its controlled rate (€560), give €3,393.
  - (4,402 + 3,393) × 0.89 = €6,938 a month; × 9.5 − 16,131 = **€49.8k**.
  - It is fragile: with E27's €2,749 as B0, net is €35.8k, a 3.
- **ROI 5.** 309 %.
- **Payback 3.** 4.17 months on the sheet's ramp. Month 4 ends at −€1,107 and month 5 at +€5,253.
- **Evidence 2.** It is the better-supported rate: it agrees with E27's September €3.9–4.3k for GjirafaMall (`fs:884`).
  - But it is one run per layer, and Gjirafa50's control is a July run.
  - I couldn't rerun it. It would be a 3 if the read-only rerun were shown live.
- **Works 2 (3).** As above.

### Framing A (levers only, lever 4 at €560)

- **Net benefit 2.** €3,393 × 0.89 = €3,020 a month; net **€12.6k**.
  - The sheet's €19.0k (`fs:408`) uses lever 4 at its *mid* case, which borrows B0's uncontrolled 5.8 % rate (`fs:197`).
- **ROI 3.** 78 %.
- **Payback 2.** 6.9 months.
- **Evidence 2.** The levers are walk-forward backtests on booked lines, but the elasticity is assumed and I can't rerun them.
- **Works 2 (3).**

### Do I accept €10,409 a month as the rate? **No.** A defensible rate from this repository is €2.7–4.4k.

- **The arithmetic checks.** (11,068 + 9,749) / 2 = 10,408.5. The per-approval spans add up: August 11,079 ≈ 11,068 and September 9,756 ≈ 9,749 (`fs:828`).
- **Approval #12 (6 Aug) is about €3,934 a month, 38 % of B0** (26 days × €281 plus 2 days × €281, averaged).
  - Only 45 of its 1,244 repriced SKUs sold in both windows (`fs:884`).
  - The team's own October note calls the August run "not separable from restocking" (`fs:920-922`).
- **Dead-stock runs** (#6, #7, #13, #19, #22) are clearance, not a recurring rate.
- **Lever 4** reuses the same uncontrolled rate, so the error compounds.

**What would change my mind (all of it would have to be in the repository):**
- E27 rerun on SKUs **in stock** in both windows (from stock snapshots, not "sold in both"), coming back near €10k;
- same-weeks controls for the October approvals #30, #32, #35 and #37, plus one Gjirafa50 run inside Aug–Sep, adding up to about €10k;
- the pilot's holdout difference in the money ledger;
- to count B0 as *new* money at all: evidence that the manual process is ending. E28 shows the opposite (§2).

## 2. The single biggest unproven assumption

**That the €10,409 is the calculator's effect *and* is lost without this build** ("Without the build, the test ends", labelled assumed, `fs:281-285`).

- B0 carries 71 % of the claim: €9,264 of €12,965.
- The causal half is cut to €2.7–4.4k by E20 low and E27.
- The "lost without the build" half is contradicted by E28 (`fs:892`):
  - GjirafaMall KS ran on 6 and 19 Oct;
  - Gjirafa50 KS ran on 7 and 21 Oct;
  - four MK/AL layers got full pushes on 21 Oct.

  The manual process sped up in October; it didn't stop.
- The organisers never ruled on framing B. `kickoff-plan.md:137` still says "Baseline framing (A or B): pending", while `:748` says "the headline follows the organisers' answer".

## 3. Claims I can't verify, and claims contradicted

### Not verifiable from this material

1. **Every runtime figure.** They all need `PricingToolHackathon`, which isn't supplied:
   - the cost watcher's 293 moves, 54 holds and 0.7–9.1 min (`one-pager.md:20-22`, `HACKATHON.md:7-9`, `kickoff-plan.md:711`);
   - 57 %, 909 raises, 96.5 % and 1.5 % (`one-pager.md:30-32`, `HACKATHON.md:191-193`, `:275-279`);
   - 49,214 / 50,969, 25,390 / 26,604 and 171,108 / 174,087 (`one-pager.md:33-34`, `HACKATHON.md:280-285`);
   - engine runs #1100 and #1101 and their SKU counts, Alpha's pass #2 with 8 rules, and Nova's held plan (`one-pager.md:36-40`);
   - "hk.DataSources: 40 rows" (`HACKATHON.md:299`);
   - logged responses #19, #24 and #38 (`HACKATHON.md:106-115`);
   - 85 calls / $10.29 (`HACKATHON.md:99`, `kickoff-plan.md:713`).
2. **"45 of 45 matched"** (`HACKATHON.md:74-75`, `kickoff-plan.md:715`). The file is self-generated. It shows reproducibility, not validity.
3. **The test counts.** "1012 passed, 1 skipped" and "692 at import" (`HACKATHON.md:62`, `:68`, `:309`; `fs:807`). The recorded run (31 Oct 16:03) predates four commits that changed code or tests:
   - `3116cab` (16:07), the margin target itself (`MarginTarget.cs`, `CheckedPricePublisher.cs`);
   - `04a10f4` (`AgentChatTests.cs`, `EvidenceHtml.cs`, `Web/Program.cs`);
   - `e86aeff` (`UsageMeter.cs`, `ClaudeCodeClient.cs`);
   - `ee42ba0` (`FloorTargetAgent.cs` and its test).

   No count is recorded for HEAD.
4. **E18 $559.09**: the transcripts aren't committed (`HACKATHON.md:261`).
5. **Holdout 0.11**: from 31 Oct exports that aren't committed (`fs:244`).
6. **The kickoff rulings** (replica, tool DB, no API key) are recorded only by the team (`kickoff-plan.md:115-128`, `8544f43`).
7. **"Registered on 2026-10-27"** (`DECLARATION.md:3`) and **"imported unchanged"** (`:18`): neither the form nor the source repo is available.
8. **"No marketplace commission applies"** (`one-pager.md:75-76`, `HACKATHON.md:156`). The export keeps vendors named "Dino Toys", "Mysu" and "YZATT" (`exports/sales_lines_ks.sql:40`). Nothing in the repo shows they are Gjirafa's own.
9. **"Run week … shown live on Monday"** (`one-pager.md:51-53`, `HACKATHON.md:134-136`). The script presses it 10 minutes *before* the demo (`kickoff-plan.md:684`).
10. **"The worked example … scores 76"** (`fs:40`) and the score bands "from the hackathon rules" (`fs:615`).

### Contradicted

- **C1. New, high: the demo's injection step writes to the real hackathon sandbox.**
  - **The claim:** `kickoff-plan.md:711` says it "runs against the test database, so it never shows in the app", and `HACKATHON.md:222-223` says injected cases run against a separate database.
  - **What the code does:**
    - `--test` redirects only the `AgentDb` connection (`AgentTools/Program.cs:88-96`).
    - The writer's sandbox connection is `AgentDataSource`, built from `PricingToolDb` (`AgentServiceCollectionExtensions.cs:37`, `NopDiscountWriter.cs:110`, `:1020`, `PublisherOptions.cs:62`). That is the hackathon database, not the test one.
    - `CostWatcher` publishes an `[INJECTED-TEST]` observation like a real one (`CostWatcher.cs:357`). The tag only marks the pass (`:441`).
    - The protect-discount rules it honours are read from the test database (`:83-85`), so the real rules don't protect anything.
  - **The likely result** (from reading the code; I didn't run it): an injected raise lands on `PricingToolHackathon`'s `shop_sandbox_*` shelf, the one the Run tab reports. Its batch and audit sit in the test database, so a rollback from the app can't find it.
- **C2. High: the headline vs its own checks.** `HACKATHON.md:19`, `:126-131` and `one-pager.md:48-51` present €10,409 with "robustness checks: E20, E27". Those checks give €4,402 and €2,749, and E20's method text rejects the uncontrolled case (`numbers.json:507`). The €2.7k is now printed next to the headline, but it is still called a "robustness check" of a number it cuts by 74 %.
- **C3. High: "not claimed" vs the claim.** `DECLARATION.md:14-16` says the calculator's measured effect "is **not claimed**". It is 71 % of the claim (`HACKATHON.md:147`). "B claims no past euro" (`fs:4`) relabels the issue; it doesn't remove it.
- **C4. High: "without the build, the test ends" vs E28.** `one-pager.md:55-61`, `HACKATHON.md:138-143` and `kickoff-plan.md:45` ("only in KS … Without v2 nothing secures that profit") vs E28's October runs and pushes (`fs:892`). Line 45 is unchanged under the dated note at `:44`.
- **C5. Medium: the headline framing was never ruled on.** `kickoff-plan.md:137` says framing is "pending" and `:748` says the headline "follows the organisers' answer". No answer is recorded anywhere, yet B is the headline.
- **C6. Medium: stale planning claims.** `kickoff-plan.md:750`:
  - "**Evidence rates 5 under framing B**", while the sheet says 3 (`fs:295-297`);
  - "Mid ≈ €10.6k … clears the net-benefit band-5 line (≈ €10.3k/month) before any new lever", while the sheet needs €12,224 (`fs:52`) and B0 × 0.89 is €9,264.
- **C7. Medium: the person's time.** It is costed at 3.9 attended hours plus 1 assumed (`fs:74-83`). Every attended row in `hours.md` ends Fri 19:58, but Saturday's commits record the user's decisions (`be566c1`, `def8b85`, `a9d135d`) and rows logged "at the user's request".
- **C8. Low: the declaration's wording.** "Exceptions inbox" (`DECLARATION.md:31`) was replaced by "Waiting for you". "Go-live ready" (`:33`) vs open prerequisites: a DBA grant, the shop's answers on its cache and `OldPrice`, and the owner's call (`HACKATHON.md:220-221`).
- **C9. Low: two minute ranges for the same cost-watch figure.** "1–9 minutes" (`HACKATHON.md:7`, `kickoff-plan.md:711`) vs "0.7–9.1" (`one-pager.md:21`). Rounding, but two versions of one figure.

## 4. Rule compliance

| Rule | Finding |
|---|---|
| Built during vs before | **Pass, with a note.** Commit 1 `b3f19a6` (16:50) holds pre-event docs and the sheet generator, disclosed at `HACKATHON.md:39-43`. Import `9c977ca` is labelled. All 201 later commits are dated after 16:50 on 30–31 Oct, with author and committer dates equal; the dates are self-reported. |
| Data access | **Depends on a ruling only the team recorded.** The rules summary says nothing connects to the replica (`rules-summary.md:7`). The code reads it every 15 minutes (`ReplicaCostFeed.cs:45`), and so does the daily mirror (`SandboxMirror.cs:73`). The tool-DB copy (`0410c08`) rests on `kickoff-plan.md:120-122`. The connection lives only in user-secrets (`AgentTools/Program.cs:101-102`). |
| Shop writes | **Pass by configuration.** `"Target": "Sandbox"` in every config (`Web/appsettings.Hackathon.json:39`, `Engine/…:30`, `AgentTools/appsettings.json:29`). The writer throws outside `shop_sandbox_*` (`PublisherOptions.cs:60-61`). The guard is on the schema name, not the server (disclosed). C1 is a sandbox-to-sandbox leak, not a shop write. |
| Secrets | **Pass.** `git grep` at HEAD and `git log --all -p -G` for `sk-ant`, `PRIVATE KEY` and `Password=` find only documents describing the check, plus code reading configuration (`EnvFileLoader.cs:29` is imported and has no callers). No `.env`, `k8s/`, CSV, backup or `settings.local.json` is tracked. |
| Personal data | **Low.** HEAD has only a placeholder (`Views/Users/Index.cshtml:18`). The import commit has an `@gjirafa.com` address in 6 files (disclosed, `HACKATHON.md:300`). The export reads `OrderId` but doesn't output it (`sales_lines_ks.sql:7`, `:57-64`). The tool-DB copy blanks email values (`Copy-ToolDbKs.ps1:17`). |
| Labelling of numbers | **Partly.** `numbers.json` labels every figure. Unsourced: the score bands (`fs:615`), "76" (`fs:40`), and every cost and ramp input (§5). B0 is labelled "measured", which is true of the before/after, not of the cause. |
| Mocks shown | **Partly.** Injected cases are tagged, but C1 means an injected cost can reach the non-test sandbox. I couldn't see the UI banner. |
| Others writing the financial case | **Note.** An organiser's feedback shaped the benefit table (prompt 15, per dry run 4). Probably guidance, not authorship (`rules-summary.md:11`). |

## 5. The financial sheet

**Arithmetic: pass.**
- One-time: (3.9 + 1 + 160 + 8) × 50 + 559.09 = 9,204.09 ✓ (`fs:383`).
- Running: 12 × (68.94 + 0 + 400) + 52 × 0.5 × 50 = 6,927.26 ✓.
- Monthly: (4,158 + 10,409) × 0.89 = 12,964.63 ✓; × 9.5 − 16,131.35 = 107,032.63 ✓; ROI 6.64 ✓.
- Payback: 3 + 4,453.59 / (8,804.6 + 4,453.59) = 3.36 ✓ (`fs:605-612`).
- Lever total (`fs:237`) and benefit by source (`fs:477`) both match.
- Lever 4's shares are consistent: 4,402 / 180,411 = 2.4 % and 10,409 / 180,411 = 5.8 %.

**Track A formula: followed.** Net = year-1 margin − all costs; ROI = net ÷ cost; payback from a monthly grid.

**Revenue counted as margin: no.** Every benefit is gross profit at booked COGS.

**Hours saved counted as money: no** (`fs:481-483`).

**Double counting:**
- None inside the sheet's arithmetic: lever 8 is scored after levers 1–6, one lever per line (`fs:225`).
- The real double count is against the baseline: B0 is money the declared calculator earns by hand today, and still did in October. All of it is booked as v2's from month 2 (`fs:445-449`).
- Lever 4 re-multiplies the same uncontrolled B0 rate onto MK/AL (`fs:197`), so B0's error appears twice.
- The 11 % holdout cut is applied to B0 too (`fs:363`). That is conservative, but it shows B0 is treated as v2's output.

**One-off gains carried as a monthly rate:**
- #12's restocking, about €3.9k a month of B0;
- dead-stock clearance runs inside B0;
- lever 3's August cost event;
- lever 8 high, October's overlapping push sets (`fs:225`), sensitivity only.

**Inputs without a source** (all labelled "assumed"):
- 1 hour of the person's time inside unattended blocks (`fs:80`), 160 h to production, 8 h training, 8 h a month maintenance, 0.5 h a week review;
- servers €0, which assumes a subscription token in production k8s that is neither built nor costed (`fs:105-109`);
- setup €0;
- the pilot's start, length and 25 % share;
- every call volume (`fs:309-342`);
- the elasticities;
- $1 = €1;
- holdout 0.11 rests on uncommitted exports.

**Self-ratings.** Evidence 3 and Works 4 (`fs:295-301`) are the team's own and add 8 points over mine.

## 6. "The AI decides, code enforces": three spot-checks

1. **Alpha's floors and targets: pass.**
   - The model's rows go through `SafetyLimits.Evaluate` (`FloorTargetAgent.cs:150`).
   - Holdout and thin-data rows are kept whatever the model says (`SafetyLimits.cs:42-45`).
   - Unknown actions, out-of-range values, action/value mismatches, target below floor and floors below the hard minimum are rejected (`:48-68`).
   - Paused rows and steps over 1 point a week are held (`:70-79`).
   - Only applied, non-keep decisions are written (`FloorTargetAgent.cs:168`).
2. **Run checker `fix_price`** (a model number that becomes a price): **pass.**
   - A fix counts only if every rule re-checks at Info or below (`RunChecker.cs:131-138`).
   - The rules block below cost, tenfold jumps, scale errors and anything above max(anchor, current) (`RunCheckRules.cs:69-89`). The model can't downgrade a block, and its escalation becomes a block (`RunChecker.cs:142`).
   - Caveats: a dead-stock fix can go down to 40 % of cost (`RunCheckRules.cs:73-74`), and the ceiling check is skipped for Mall SKUs synced to Gjirafa50 (`:88`).
3. **A chat `margin-target` rule** (model output that raises shelf prices): **partial.**
   - **What code checks:** the kind, the dates, a value strictly between 0 and 100, and one layer (`AgentChat.cs:451-460`). `MarginTarget.Solve` never lowers a price and never goes above max(regular, today's price) (`MarginTarget.cs:93-94`).
   - **What it doesn't check:**
     - how plausible the value is (anything up to 99 %);
     - how long the date range is;
     - whether the number matches the person's message. Code records the model's reading under the person's name (disclosed at `HACKATHON.md:94`).
   - **Consequence:** a misread "15" as "51" would raise every product under 51 % margin to its regular price for every day in the range, and publish it. The same gap applies to `NoDepthCap`, which lifts the cap from 25 % to 90 % (`AgentChat.cs:400`, `:538`; `CampaignPlanning.cs:114`), and to the binding goal.
   - Campaign prices themselves stay bounded at cost ÷ (1 − 3 %) (`CampaignPlanner.cs:114`, `CampaignLimits.cs:33`).

## 7. Dry run 4's findings now

| Dry run 4 finding | Status now | Evidence |
|---|---|---|
| §1 rate not accepted / §2 biggest assumption | **Not resolved** | B0 unchanged; E27 €2,749 and E28 unchanged |
| #11 headline vs robustness checks | **Partly**: the €2.7k sentence is now in both documents; the label and the headline stand | `HACKATHON.md:128-130`, `one-pager.md:50` |
| #12 "not claimed" vs the claim | **Not resolved** | `DECLARATION.md:14-16` unchanged |
| #13 E28 vs "test ends" | **Not resolved** | `kickoff-plan.md:45` unchanged |
| #14 stale headline | **Resolved** | `HACKATHON.md:123` = `fs:5` |
| #15 stale AI figures | **Resolved** | `HACKATHON.md:99-101` = E15 and E18 |
| #16 "rules only tighten" | **Resolved** (wording) | `HACKATHON.md:94` |
| #17 depth cap 25 % | **Resolved** (wording); the lift is still a model boolean | `HACKATHON.md:91`, `AgentChat.cs:400` |
| #18 demo injection step | **Wording changed, defect remains in another form** | C1 |
| #19 person's time | **Not resolved** | `fs:74-83` |
| #20 declaration wording | **Not resolved** (kept on purpose) | `DECLARATION.md:31`, `:33` |
| §6 item 3 / #36 chat switches from the model | **Disclosed only** | `HACKATHON.md:94` |
| #40 `pass.Applied` counts keeps | **Resolved** | `FloorTargetAgent.cs:171`, `FloorTargetAgentTests.cs:144`; tests not rerun |
| Database, transcripts, rulings, holdout source, score bands, unsourced costs | **Not resolved** | §3, §5 |
| Organiser shaping the financial case | **Unchanged note** | — |
| Net benefit 5 is fragile | **Not resolved** (about 6 % headroom) | `fs:52` |

**Summary.**
- The team fixed every wording and arithmetic defect dry run 4 named.
- Every finding about the substance of framing B is still open: the cause of B0, whether it is new money, E28, the declaration, and the missing ruling on framing.
- The demo fix moved the problem rather than removing it (C1).

I score B at **64 (72)** at the claim and at the controlled measurement, and A at **42 (46)**. That is 20 points under the team's 84.