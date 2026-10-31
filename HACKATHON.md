# Pricing Tool v2: Gjirafa AI Hackathon 2026, Track A (Money)

Two AI agents on top of the existing pricing calculator (v0.5). **Alpha**, the pricing agent, sets each price band's
floor and target per main category. **Nova**, the campaign agent, writes campaign briefs, and Alpha turns each one into a
plan that code computes. A publisher writes the shop's prices itself, cuts as NOP discounts and raises as the store's
regular price (only to the `shop_sandbox_*` schemas during the hackathon), and a cost watcher checks costs every 15 minutes:
on 31 Oct it read the morning's 293 real cost moves within 1–9 minutes of the replica's refresh, raised none (no rise put a
price we set under its floor) and held 54 for a person (52 under the floor at a cheaper deal: 28 the calculator's own
earlier push, 21 only an app-only deal, which no longer holds a price, 3 a team deal; 2 with a floor above the shelf; E22). **The AI decides; code enforces:** no model
output changes a price, a band or a discount without passing limits written in C#.

The app has four tabs: **Run** (the current run, treated as live: what it changed, its expected effect on the shelf, Alpha's
account of it, Run week), **Chat** (the agents' activity, the chat with them, and only what they couldn't settle
themselves), **Campaigns** (the calendar, and a conversation with Nova about each campaign) and **Proposals**. The proof
lives in files, not in the app: [`evidence.html`](docs/hackathon-2026/evidence/evidence.html), the financial sheet and the
one-pager.

**The money claim** (section 4): €12,965 a month at full scope, 84 points. It is the calculator's measured rate, B0 =
€10,409 a month (measured before and after each approval; robustness checks: E20, E27), which the build makes permanent,
plus the levers this build adds.

Everything below can be checked from this repository, except what needs the hackathon database (`PricingToolHackathon` on
LocalDB): it holds the company's sales data, so it is not in git. Those figures' aggregates are in
`docs/hackathon-2026/evidence/numbers.json` and `docs/hackathon-2026/backtest-results.md`, each with the script that
reruns it against the database (the stored backtest run itself is committed as
[`evidence/backtest-snapshot.json`](docs/hackathon-2026/evidence/backtest-snapshot.json), aggregates only; `*.csv` stays
out of git by rule). Figures carry an ID from [`docs/hackathon-2026/evidence/numbers.json`](docs/hackathon-2026/evidence/numbers.json), each with
its label (measured / estimated / assumed), method, source and the script that reruns it. The
[one-page summary](docs/hackathon-2026/one-pager.md) and the [financial sheet](docs/hackathon-2026/Financial%20sheet.xlsx)
are generated from the same file (`docs/hackathon-2026/financial/build_financial_sheet.py`, `build_one_pager.py`).

## 1. What was written during the hackathon

- **Commit 1** (`b3f19a6`, Fri 30 Oct 16:50) is documentation. **Commit 2** (`9c977ca`, 16:50) is the labelled import of the
  existing calculator from the main repo at `b184ff55458c3fe0cc4dfe88a8e3aa38ac8c5f42`: declared, not hackathon work.
- **Everything after `9c977ca` is hackathon work**, made after the 16:30 kickoff:
  `git log --format='%h %ad %s' --date=iso 9c977ca..HEAD` (197 commits at `3116cab`, 31 Oct 16:07; recounted at the
  freeze).
- Commit 1 also carries what was prepared before the kickoff: the plan, the registration text (`DECLARATION.md`),
  `hours.md`, the draft financial sheet and its generator (`financial/build_financial_sheet.py`, rewritten since),
  `excel_recalc.ps1`, the instructions for Claude (`CLAUDE.md`), `.gitignore`, and a Word document on the shop
  platform's discount integration (`docs/Dynamic Discounts Platform integration.docx`). They are the plan and the
  financial case's tooling, not solution code; the organisers can rule on that.
- `git diff --shortstat 9c977ca..HEAD`: 373 files changed, 97,150 insertions, 318 deletions at `3116cab` (migrations
  and their designer files included). The counts below are new files at that commit (`git diff --name-only
  --diff-filter=A 9c977ca..3116cab -- <project>`).

### New vs reused

| | Files | What |
|---|---|---|
| **New project `PricingTool.Agent`** | 149 | The agents: Alpha (`Alpha/`), Nova and campaign planning (`Campaigns/`), the run checker (`Checker/`), the outcome tracker, daily cycle, money ledger, time sheet, exceptions inbox (`Operations/`), the chat (`Chat/`), the backtest (`Backtest/`), the Claude Code client (`ClaudeCode/`), the data tools (`Tools/`), prompts (`Prompts/`), its own schema `ai.*` and migrations (`Storage/`) |
| **New project `PricingTool.AgentTools`** | 5 | The agents' read-only data tools as an MCP server, and every command used to run, check and reproduce (`backtest-*`, `reproduce-per-ty`, `plan-campaign`, …) |
| **New project `PricingTool.NopIntegration`** | 22 | The NOP discount writer (create, update in place, expire, time-boxed campaigns, round-trip check, audit and rollback) and the regular-price writer, the sandbox and its mirror of the live shop, the cost watcher and its replica feed |
| **New in `PricingTool.Web`** | 32 | `RunController`, `ChatController`, `CampaignsController`, `AgentController` (the agents' actions), `OperationsController`, `BacktestController`, `EvidenceController` and their views, with `RunReportService`, `AgentActivity`, `ChatTurnLauncher`, `ProblemPassLauncher`, `RunWeekLauncher`, `RunPicker`, `EvidenceService` and `EvidenceHtml`: the Run, Chat and Campaigns tabs, Run week, the back-office pages (daily cycle, time sheet), and the writer of the evidence file. The Evidence and Backtest pages still exist but are no longer linked: the proof lives in files |
| **New in `PricingTool.Engine`** | 3 | The hackathon host: `DailyCycleWorker`, `CostWatcherWorker`, `appsettings.Hackathon.json` |
| **Changed in the imported projects** | 30 | Per-category floors (`Core/Domain/CategoryBands.cs`, `PriceBandConfig.cs`, `MarginTargetService`, `Data/Entities/BandCategoryRule.cs`, `PricingRunCategoryRule.cs`, `PricingToolDbContext`, `DependencyInjection`, `BandConfigProvider`, `PricingRunOrchestrator`, `CategoryMapReader`); `RoundingService.IsOnGrid` (the run checker's off-grid test); the hackathon environment and its guard (Web and Engine `Program.cs`, `launchSettings.json`, `.csproj`); the employee email removed (`appsettings.json`, `PortaOptions.cs`, `User.cs`, `DbSeeder.cs`); the navigation (Run · Chat · Campaigns · Proposals, an Admin menu), the layout and its styles, and the waiting badge (`_Layout.cshtml`, `site.css`, `WaitingBadgeViewComponent`, `ViewModels.cs`); the Web `Program.cs` also writes the evidence file (`--evidence-html`). `git diff --name-only 9c977ca..HEAD -- PricingTool.Core PricingTool.Data PricingTool.Engine PricingTool.Web` lists them with the new files |
| **Tests (`PricingTool.Tests`)** | 36 | New test classes for every agent, limit, publisher path and backtest lever |
| **Docs and scripts** | 75 | `docs/hackathon-2026/` (plan, hours, prompts, the data dictionary, backtest results, failure cases, evidence), the export SQL, the laptop scripts, `GO-LIVE.md`, `GO-LIVE-LAPTOP.md` |

Reused: the calculator's engine (changed only in the files listed above), the band and rounding logic it already had, its Web pages (proposals, bands,
schedule, audit log) and its tests (692 at import, all still passing).

## 2. How to run it

1. Restore the hackathon database `PricingToolHackathon` on `(localdb)\mssqllocaldb` (the daily backups in
   `%LOCALAPPDATA%\PricingToolAgent\backups`; the data is not in git) and log Claude Code in once (`claude auth login`).
2. `dotnet build PricingTool.sln` and `dotnet test PricingTool.sln` (1012 passed, 1 skipped: the opt-in real headless call; E17).
   The evidence is a file:
   `dotnet run --project PricingTool.Web --no-build --launch-profile hackathon -- --evidence-html docs/hackathon-2026/evidence/evidence.html`
   writes [`evidence.html`](docs/hackathon-2026/evidence/evidence.html): every figure of `numbers.json` with its label,
   method, sources (and when each was loaded) and where it is used (the one-pager, the financial sheet's cells, this file),
   and the figures whose script is a SQL file (11 files: E12–E14, E20–E23, E25–E28) rerun as it is written, as
   `evidence_reader`, a database user that can only read, each cell compared with the stored value (31 Oct 16:3x: 45 of
   45 matched, 34 s).
3. The web app: `dotnet run --project PricingTool.Web --launch-profile hackathon` → http://localhost:5058 (the app and the
   engine refuse to start in any other environment)
   (Run, Chat, Campaigns, Proposals; an Admin menu for the daily cycle, the audit log, the time sheet and users).
4. The engine host (daily cycle at 07:00 plus the 15-minute cost watcher): `powershell -ExecutionPolicy Bypass -File scripts\run-laptop-host.ps1`.
5. Reproduce the figures: `PricingTool.AgentTools backtest-snapshot`, then
   `powershell -ExecutionPolicy Bypass -File docs\hackathon-2026\evidence\build-numbers.ps1`.

## 3. Where a model's output becomes a decision

The models (Claude Opus 5.5 through Claude Code headless, `claude -p --output-format json`; the run checker on Sonnet 5.5)
answer in a JSON schema. Code parses it, retries once on a bad answer, and otherwise holds; then code decides.

| Agent | The model proposes | Code decides | Where |
|---|---|---|---|
| **Alpha, weekly floors and targets** | keep / raise / lower per band × category, with reason and evidence | `SafetyLimits.Evaluate`: a step of at most 1 point a week counted from last week's value, the layer's hard minimum, target ≥ floor, thin data kept, holdout categories untouched, paused categories held; then `AgentDecision` rows (applied / held / rejected) and, for applied ones, the `BandCategoryRule` the engine prices with | `PricingTool.Agent/Alpha/FloorTargetAgent.cs:149` → `Alpha/SafetyLimits.cs:37` → `FloorTargetAgent.cs:165` |
| **Nova, weekly briefs** | campaign briefs (dates, scope, goal, depth, minimum margin) and lessons | `CampaignLimits.Evaluate`: lead time, calendar window, length, no overlap with a main campaign, holdout, two minis at once, depth cap 25 %; a minimum margin never under 3 % (code raises it, also in review rounds); a goal a person set overrides Nova's | `Campaigns/CampaignAgent.cs:150` |
| **Campaign planning** | Alpha chooses one of four strategies code computed; Nova accepts or adjusts | `CampaignPlanner` computes every price (charm-rounded, never under cost or at today's price, depth cap, holdout, minimum margin of at least 3 %); only a listed strategy is accepted; a person's goal can't be changed by Nova; a plan short of it is held | `Campaigns/CampaignPlanning.cs:352`, `:190` |
| **Run checker** | pass / hold / fix per flagged row of an engine run | `RunCheckRules` judge every row in code; the model can't downgrade a rule's block to a pass or a warn: it can only replace the price with one that passes every rule again (then the row is "fixed" and that price is exported); anything the model escalates becomes a block | `Checker/RunChecker.cs:79`, `:132` |
| **Chat** | a reply, a brief, or a rule | briefs go through `CampaignLimits` and the planning loop; rules only tighten (exclude a brand or category, raise a campaign's minimum margin) and campaign planning enforces them; what-if figures come from code (`what_if_floor`) | `Chat/AgentChat.cs:228` |
| **Cost watcher** | nothing: no model | raises to the floor, holds a data-error jump, ends a discount above the shelf, judges a live campaign on its own minimum margin | `NopIntegration/CostWatch/CostWatcher.cs` |
| **Publisher** | nothing | with `Publisher:Target` = Sandbox (every config in this repo) the writer refuses any schema but `shop_sandbox_*`; every write is audited first so a rollback restores it exactly; a raise above the run's max(anchor, current) is refused again here, and a regular price someone changed since the run is never overwritten | `NopIntegration/PublisherOptions.cs:58`, `NopRegularPriceWriter.Decide` |

Every call is logged in `ai.AiCallLog` (model, tokens incl. cache, cost, duration, turns), and every data tool call in
`ai.AgentQueryLog`. The agents' runtime Claude calls: 53 calls, $7.90 in the live database and 5 calls, $1.03 in the test
database (E15). The two Claude Code sessions that built this used $393.76 since kickoff (the three AI-judge dry runs included), $298.03 of it in the unattended
blocks (E18; tokens measured from the session transcripts, priced at API list prices: API-equivalent, the actual spend is
the flat Claude subscription).

### Logged responses (from `ai.AiCallLog.Output`; emails are redacted by the client, none appeared)

- **#19, Nova, weekly campaign pass (30 Oct 21:49).** Its brief for Black Friday 2026, excerpt: `{"kind":"calendar","calendarId":18,
  "name":"Black Friday 2026 – GjirafaMall own assortment",…,"goalType":"gross-profit","goalValue":27000,"minMarginPct":14,"maxDepthPct":25,
  "exclusions":["Holdout categories Sport and Vegla Pune",…]}`. The user found the goal too low; Nova had anchored on Black Friday
  2024 unscaled. That led to the growth-scaled anchors, the lessons memory and the binding person-set goal.
- **#24, Alpha, campaign plan choice (30 Oct 22:38).** `{"strategy":"visible","explanation":"Black Friday is a main calendar campaign,
  so I chose visible. It discounts 2,000 of 2,286 SKUs (average depth 18.6%) … The 57,456 EUR gross-profit goal cannot be reached under any
  strategy within the limits …"}`. Nova then lowered the person's goal to pass the plan, which is how the binding-goal check was found.
- **#38, Alpha, chat (30 Oct 23:56).** `{"reply":"If the floor for Gjirafa50 band 26 (cost €10–50) Aksesorë were 15% instead of today's 13%,
  gross profit would go up by about +€92 a month at elasticity −2.0, +€116 at −1.0 and +€129 at −0.5. These are estimates from what_if_floor …"}`:
  the figures are the tool's, computed by the backtest's floor lever.

The prompts are the fixed rules in `PricingTool.Agent/Prompts/*.md` plus a data block code builds per call (e.g.
`CampaignAgent.BuildPrompt`, `AgentChat.CallAsync`); the agents never see the user's or this repo's instructions (an
isolated runtime folder, `--setting-sources ""`, no built-in tools, only our read-only MCP tools).

## 4. The numbers: one claim

**The claim: €12,965 a month at full scope, 84 points** (year 1: net benefit €107,205, ROI 672 %, payback 3.3 months; the
[financial sheet](docs/hackathon-2026/Financial%20sheet.xlsx), with the holdout categories' share left out).

**Two proofs.** *The engine works:* the Aug–Sep test on the real shop, B0 = €10,409 a month (GjirafaMall €8,006,
Gjirafa50 €2,403; E20), measured before and after each approval from the shop's booked sales lines with booked COGS;
robustness checks: E20, E27 (both rerun whenever the evidence file is written). Gjirafa50 KS's figure is mostly its 21 July run
carried forward, plus the three dead-stock runs (E20 lists each month by approval).
[`evidence/e20-test-phase.sql`](docs/hackathon-2026/evidence/e20-test-phase.sql) recomputes it from this repo's data; its
sales lines reproduce the tool's own 30-day profit before each of eight approvals within 2 % (E26), which checks the
data, not the cause. *It runs by itself:* this build. Run week on Gjirafa50 KS, shown live on Monday on fresh data: the
proposed prices, the check, the publish to the local sandbox with its rollback, the read-back, every number traced to a
query.

**Without the build, the test ends** (assumed): no one runs the engine by hand as a permanent process. What is measured
(E28): one person has run it by hand since 21 Jul, with no schedule. GjirafaMall KS ran on 21 Jul, 6 Aug, 3 Sep, 6 Oct
and 19 Oct; Gjirafa50 KS's regular prices were set on 21 Jul and not refreshed for 78 days (dead-stock runs on 3 Aug,
19 Aug and 3 Sep); on MK/AL, in Aug–Sep only GjirafaMall AL had a full push. Even when it ran, 19 % of the units on raised
products still sold at the old price in Aug–Sep (E23; late October's 70–84 % was the month two push sets overlapped). So
the build's value is the stream it makes permanent, plus the levers:

| Where the claim's benefit comes from | € a month | Label |
|---|---|---|
| The stream the build makes permanent: B0, the calculator's measured rate | €10,409 | rate measured; "ends without the build" assumed (E20, E28) |
| Recovered leakage: raises that reach the shelf (lever 8) | €249 | estimated (E24) |
| Cadence: prices daily, not on a few manual dates (lever 5) | €396 | estimated (E5) |
| MK/AL coverage at Kosovo's tested rate (lever 4) | €1,325 | estimated (E4; €560 at E20's same-weeks rate; MK/AL had no two-month test of their own) |
| The other levers: loss block, category floors, cost reaction, learning, campaign planning (1, 2, 3, 6, 7) | €2,188 | estimated (E1–E3, E6, E7) |
| The holdout categories' share (about 11 % of the changed prices; the agents never change them), left out | −€1,602 | estimated |
| **The claim, at full scope** | **€12,965** | |
| Hours saved | not counted | hours, never euros |

GjirafaMall lines are Gjirafa's own vendors only, at booked cost: no marketplace commission applies. Cancelled orders are
excluded; returns after delivery are not netted out.

**The levers** (estimated; walk-forward on booked sales lines, each line counted by one lever, elasticity −1, assumed;
every lever on the calculator's era, Aug–Sep 2026):

| ID | Lever | € a month |
|---|---|---|
| E1 | Loss-sale block | 76 |
| E2 | Per-category floors | 2 |
| E3 | Cost reaction | 1,327 |
| E4 | Coverage MK/AL, at the calculator's Kosovo rate (E20) | 1,325 |
| E5 | Cadence | 396 |
| E6 | Learning, as built | 0 (the plan's plain rule would have cost €1.0–1.3k in September) |
| E7 | Campaign planning (12 months of campaigns) | 783 |
| E24 | Raises reach the shelf (lever 8, the publisher) | 249 |
| **E8** | **Total** | **4,158** |

Levers 1, 2, 3 and 5 raise prices, and v2 doesn't raise a price a team deal holds (the publisher leaves it to a person).
Their gain on lines sold under a team deal (a product discount at a price no approval gave the SKU), €378 a month, most of
it Gjirafa50's levers 1 and 3, is left out of the claim. Lever 8 is what v2's publisher adds over the CSV import: the
units held at the old price by the calculator's own earlier push or a regular price never raised (v2 removes both: one
discount per SKU, ended with a regular-price raise), not those held by a team deal (€1,274 a month in Aug–Sep, left to a
person, not counted). It is scored in the backtest after the other levers, so a line another lever scores counts once
(E23's whole spans give €300). The stored run (#5) reads each day's lines in sale-time order; before, the database's
order moved lever 3 by €35 a month. (A 12-month window, tried after dry run 1, scored levers 1 and 3 against months before
the calculator; dry run 2 caught it.) Each lever's sensitivity to other elasticities is in
[`docs/hackathon-2026/backtest-results.md`](docs/hackathon-2026/backtest-results.md) and the sheet's sensitivity columns;
the Backtest tab shows the stored run and its CSV.

**Where the levers come from.** Mostly automation, not the model's own choices: the loss block and the cost reaction
(levers 1 and 3: rules in code and the cost watcher), daily checked prices (5: the cycle writes them every day, the team
still pushes the file), raises that reach the shelf (8: the publisher) and coverage of every layer (4). Most of levers 1,
3 and 5 are price raises; a discount can't make one, so since prompt 07 the publisher writes a raise as the store's
regular price (`TierPrice`), built and tested on the sandbox, not live. On 31 Oct's Run weeks (Gjirafa50 #2 and
GjirafaMall #3; the 98.3 % below is on later runs, #1109 and #1107) 99,197 of the two KS layers' 172,579 checked prices
reached the sandbox shelf at our price (57 %), 909 of them raises (1,167 raises were written; 258 sit under a cheaper
deal); what didn't was almost all a cheaper discount we didn't write (73,379), and 3 prices someone changed after the run.
Those discounts were mostly not team deals but the calculator's own earlier pushes (section 5, the caveat on raises). The
model's own decisions are the per-category floors (lever 2: €2 a month in the backtest, because Alpha's rules move a
floor one point a week) and the campaign plans (lever 7 replays the planner's per-SKU depth, which code computes; the
model chooses among the strategies). What the agents add so far is running the whole loop every day, on every layer,
without a person; how much their own judgment is worth is what the pilot's holdout measures.

**The financial sheet** (live formulas; inputs typed from `numbers.json` with their IDs) gives the claim, its Track A
score and where its benefit comes from (the "Benefit by source" sheet); the same model's other columns are labelled
sensitivity (the levers alone, other elasticities) and a robustness check of B0, not alternative claims. Payback is what
the sheet computes from the ramp: built in month 1, a two-month pilot at an assumed quarter of the benefit, then full
scope (9.5 full-benefit months); the pilot's assumptions are not tuned to cross a line. Costs: the attended hackathon
hours at €50 (E16) plus an assumed hour of the person's time inside unattended blocks, 160 assumed hours to production
(including review of the AI-written code), 8 hours of training and sign-off of the limits, the build sessions' AI usage
(E18), 8 maintenance hours a month, half an hour of exception review a week, and the agents' AI cost from the measured
cost per call (E19) × an assumed monthly volume. The Evidence (3) and Works (4) ratings in it are our own judgment, with
the reason next to each.

## 5. What is mocked, injected or not finished

- **Publishing is to the sandbox only.** `Publisher:Target` is Sandbox in every `appsettings.Hackathon.json`; the writer
  then refuses any schema outside `shop_sandbox_*`. Nothing writes to a shop database. The sandbox holds the shop's
  discount tables and its regular prices (`TierPrice`, seeded from the 31 Oct KS copy), each with the live DDL. Since
  31 Oct a daily mirror step (`mirror-L<layer>`, before the publish) brings the live shop's other discounts, their product
  mappings and flags, and the regular prices the publisher hasn't written into it for the run's products, read-only from
  the replica, so each day's publish measures what would reach the live shelf (the October sets ended that night; without
  it the sandbox would describe a shop that no longer exists). Going live is configuration, a DBA's grant, the shop's
  answers on its price cache and the `OldPrice` convention, and the commercial owner's call on the supersede switch
  (`GO-LIVE.md`); the live path is built and tested against the sandbox, never run against the shop.
- **Injected test cases** run against a separate database, `PricingToolHackathonTest`, and are tagged `[INJECTED-TEST]`
  in data, logs and UI. The live database holds no mock data; `PricingEngine:UseDemoData` is false.
- **Not finished:**
  - **The Për ty reproduction test fails by one point** (E11): the agents' plan expects 14.9 % margin (hand plan 15.1 %,
    within bounds) but keeps 69 % of normal margin (67 % on the hand plan's own base) against the 70–80 % required. What
    happened under the hand plan: 14.8 % on Mall's own SKUs, €39.6k GP (E12).
  - **The planner's campaign estimates aren't calibrated yet:** too few comparable past campaigns for a leave-one-out
    factor (E10); about 44 % of a Mall campaign's GP comes from SKUs with no recent sales (E9).
  - **The Monday report** is written in the chat; its email is not built (the address isn't confirmed).
  - **The laptop start-up task** is registered but disabled (the user's step); the host is started by hand (restarted
    31 Oct 16:04 on the current build), so a reboot stops the daily cycle until someone starts it.
  - **Setting a price on the Proposals tab doesn't publish it** (the app redesign's step 5): prices go out through the
    daily cycle, Run week or the checked push file.
  - **A shop's effective margin for some days** (a `margin-target` rule given in the chat; GjirafaMall 19.5 % and Gjirafa50
    15 % on 1–3 Nov, the user's) is tested but first applies in the 1 Nov 07:00 cycle. At the prices going out that
    morning both shops are estimated above target (20.4 % and 19.9 %, volume unchanged), so it should raise nothing;
    Gjirafa50's booked margin is lower (12.3 % on the same products) because the live shop still charges its own prices.
  - **A person's campaign terms** (no depth cap, every SKU in stock, the engine's discount optimization off on the
    campaign's days, a margin goal spent down to) are tested; Java e Portokalltë 2026 (4–9 Nov, both shops) is to be
    planned again with them, and the discount-optimization switch first flips on 4 Nov.
  - **Minutes saved per task** are not set, so the time sheet exports no rows yet.
  - **Campaign what-ifs** go through a brief and the planning loop; only the floor what-if has its own tool.
  - **To the live shop, the checked push file is still the path.** The daily cycle writes it, and since prompt 07 also
    publishes the same checked prices to the sandbox (`publish-L<layer>`, from the 1 Nov cycle on the restarted host). The
    whole loop on demand is **Run week** on the Run tab (Alpha → engine → run checker → the checked prices to the
    sandbox → read-back; `RunWeekLoop`). Cuts go out as discounts, raises as the store's regular price
    (`NopRegularPriceWriter`); the cost watcher's raise to the floor goes the same way.
  - **The money ledger** has measured nothing yet: no pushed change has completed its 14-day window.
- **Known limitations** (several found by our own AI-judge dry runs, `docs/hackathon-2026/ai-judge/dry-run-1.md` and
  `dry-run-2.md`):
  - E20's high case (its 2025 season adjustment) is quoted from the main repo and not part of the claim; its low and mid cases and E21 are
    recomputed here (`evidence/e20-test-phase.sql`, `evidence/e21-by-cause.sql`). E21 is not one leak the agents fix:
    of the €14.7k a month sold under the hand-set floor in Aug–Sep, €3.0k is allowed or not a price (dead stock,
    vouchers), €4.2k is GjirafaMall SKUs priced by Gjirafa50, and the rest sits in the lanes of levers 1 (€1.4k),
    3 (€1.1k) and 5 (€4.6k, which the backtest can't score) and team deals (€0.4k). E20's controlled
    low case is one run per KS layer, not a randomized test (a robustness check, not the claim). B0 is the declared
    calculator's effect; the declaration did not claim its past result as hackathon benefit, and the claim counts the
    next 12 months (section 4). The year-over-year margin (E25) is not evidence for B0: Kosovo's margin ran +8.6 points
    over the year before in Nov 2025–Feb 2026, with no calculator.
  - E18 rests on the Claude Code session transcripts, which are not in the repo (they hold the whole build conversation).
  - Lever 1 assumes a blocked loss sale costs nothing else (no lost basket); levers 2, 5 and 6 rest on two months.
  - Lever 3's central case is mostly one event: GjirafaMall's lever 3 at e −1 is €2,554 in August and €184 in September
    (the stored backtest run), so the Aug–Sep average assumes an August-sized cost event every other month; only the low
    case (September alone) leaves it out.
  - The campaign planner's per-SKU floor (3 %) is configuration (`CampaignLimits.MinMarginFloorPct`); set to 0 it would
    turn the floor off. Every configuration in the repo has 3.
  - The kickoff rulings (reading the replica and the tool database, no API key) are recorded by us in
    `kickoff-plan.md` section 3, from the organisers' answers at the kickoff; their written confirmation would make
    them checkable.
  - A goal typed in the chat is the person's, binding on Nova, but the number stored is Nova's reading of the message,
    not one code parsed (`ai-judge/code-review-1.md`, finding 3).
  - Raises are written as the regular price on the sandbox only; going live needs the `TierPrice` grant and the shop's
    answer on its price cache and the `OldPrice` convention (`GO-LIVE.md`). A cheaper discount we didn't write still wins
    over our price (raise or cut) until a person supersedes it: on the morning of 31 Oct that held 43 % of the checked
    prices off the sandbox shelf. It was mostly not team deals: 96.5 % of the held prices carried a price one of the calculator's own
    approvals gave that SKU (its October pushes, which the team imports as discount sets, all ending by 4 Nov); over a
    third of GjirafaMall's (23.5 % of all) were app-only, which don't set the web price (written for the web since
    Prompt 10); real team deals held 1.5 % of the checked prices. With a go-live switch that supersedes only the
    calculator's own earlier pushes (single-product, carrying an approved price), 171,108 of 174,087 checked prices
    (98.3 %) reached the sandbox shelf (measured 31 Oct on runs #1109 and #1107, then rolled back); the rest were team
    deals (2,969) and 10 prices changed since the run. **The user turned the switch on that afternoon**
    (`SupersedeTeamRegularDiscounts`), and also held back the margin target trim's cuts (`HoldBackTrimCuts`: the shelf
    estimate showed cuts landing while cheaper deals blocked most raises). Republished: 49,214 of 50,969 GjirafaMall prices
    (run #1107, batch #10164) and 25,390 of 26,604 Gjirafa50 prices (#1109, #10162) are on the sandbox shelf; real team
    deals still hold 1,754 and 1,214. Their expected effect is on the Run tab (estimated, the last 30 days' units).
    Today's process stacks its own pushes: approval #37's set was imported on 20 Oct while #32's stayed live to 31 Oct,
    and the shop applies the cheaper, so #37's raises didn't reach the shelf. That is why v2 keeps one discount per SKU,
    updated in place, and ends its own in the same transaction as a regular-price raise.
  - With `Publisher:Target` = Sandbox the guard is on the schema name (`shop_sandbox_*`), not the server.
  - The hackathon's 8,000-request bar: the agents make about 290 calls a month by design (one per run or pass, never
    one per product; 85 live calls so far, E15), and no call is made to raise a count.

## 6. Rule compliance

| Rule | Proof |
|---|---|
| Read-only data access; no shop writes | The replica connection exists only in user-secrets (`ConnectionStrings:SourceReadOnly`); the agents' `query_data` runs SELECT-only on `agent.*` views with a 30 s timeout and 500-row cap (`QuerySqlGuard`, tested); the publisher writes only `shop_sandbox_*` |
| Every copied dataset is logged | `hk.DataSources`: 40 rows (source, time, row count, the SQL file in `docs/hackathon-2026/exports/`) |
| No personal data | Sales lines carry no order, customer, email, name or address; the AI call log redacts emails. The imported calculator's default admin was an employee's work email: removed from this tree on 31 Oct (it remains in the import commit) |
| No live system written | The replica and a one-time read-only copy of the tool database are read, both allowed at the kickoff. On 31 Oct a web start with the wrong launch profile (Development) migrated and seeded the main repo's *local* development database `PricingTool` on this laptop. Five minutes later, after a backup, its schema changes were reverted (every object created at 00:20:58–59 dropped). The demo data it seeded is still there (one demo run #40010 with its 360 proposals, one day of snapshots and KPIs, and 360 updated dead-stock states): the backup was taken after the start, so the updated rows' earlier values can't be restored, and removing only the inserted rows would leave that database inconsistent; the user decides (`docs/hackathon-2026/ai-judge/dry-run-1.md`). The web app and the engine now refuse to start in any environment but Hackathon |
| No secrets in the tree | `git grep -i -E "password|pwd=|api[_-]?key|secret"` finds only code that reads secrets from configuration and docs that say where they live |
| No AI-reviewer text | None anywhere; the plan forbids it |
| Hours as a cost line | `docs/hackathon-2026/hours.md`: 3.9 attended hours, the rest unattended blocks (E16 counts them); unattended blocks count as AI cost only, $345.49 API-equivalent; $527.40 since the kickoff (E18) |

## 7. Tests

`dotnet test PricingTool.sln`: 1012 passed, 1 skipped (31 Oct, 16:03). The twelve failure and unusual cases the plan names
(API down, timeout, malformed output, thin data, missing cost, kill switch, auto-rollback, unreachable campaign goal,
overlapping discount, injected cost rise, data-error cost jump, bad `query_data`) map to their tests in
[`docs/hackathon-2026/failure-cases.md`](docs/hackathon-2026/failure-cases.md).
