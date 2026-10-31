# Pricing Tool v2: Gjirafa AI Hackathon 2026, Track A (Money)

Two AI agents on top of the existing pricing calculator (v0.5). **Alpha**, the pricing agent, sets each price band's
floor and target per main category. **Nova**, the campaign agent, writes campaign briefs, and Alpha turns each one into a
plan that code computes. A publisher writes the shop's prices itself, cuts as NOP discounts and raises as the store's
regular price (only to the `shop_sandbox_*` schemas during the hackathon), and a cost watcher checks costs every 15 minutes:
on 31 Oct it read the morning's 293 real cost moves within 1–9 minutes of the replica's refresh, raised none (no rise put a
price we set under its floor) and held 54 for a person (52 under the floor at a cheaper deal: 28 the calculator's own
earlier push, 21 only an app-only deal, which no longer holds a price, 3 a team deal; 2 with a floor above the shelf; E22). **The AI decides; code enforces:** no model
output changes a price, a band or a discount without passing limits written in C#.

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
  `git log --format='%h %ad %s' --date=iso 9c977ca..HEAD` (129 commits at 31 Oct 01:16, before dry run 2's fixes).
- Commit 1 also carries what was prepared before the kickoff: the plan, the registration text, `hours.md`, the draft
  financial sheet and its generator (`financial/build_financial_sheet.py`, rewritten since) and `excel_recalc.ps1`. They
  are the plan and the financial case's tooling, not solution code; the organisers can rule on that.
- `git diff --shortstat 9c977ca..HEAD`: 268 files changed, 57,015 insertions, 216 deletions at 31 Oct 01:16 (migrations
  and their designer files included).

### New vs reused

| | Files | What |
|---|---|---|
| **New project `PricingTool.Agent`** | 115 | The agents: Alpha (`Alpha/`), Nova and campaign planning (`Campaigns/`), the run checker (`Checker/`), the outcome tracker, daily cycle, money ledger, time sheet, exceptions inbox (`Operations/`), the chat (`Chat/`), the backtest (`Backtest/`), the Claude Code client (`ClaudeCode/`), the data tools (`Tools/`), prompts (`Prompts/`), its own schema `ai.*` and migrations (`Storage/`) |
| **New project `PricingTool.AgentTools`** | 5 | The agents' read-only data tools as an MCP server, and every command used to run, check and reproduce (`backtest-*`, `reproduce-per-ty`, `plan-campaign`, …) |
| **New project `PricingTool.NopIntegration`** | 15 | The NOP discount writer (create, update in place, expire, time-boxed campaigns, round-trip check, audit and rollback), the sandbox, the cost watcher and its replica feed |
| **New in `PricingTool.Web`** | 22 | `AgentController`, `CampaignsController`, `BacktestController`, `OperationsController` and their views: the Agent tab, the Campaigns tab, the exceptions inbox, the chat, the backtest page, the ledger, the time sheet, the daily cycle |
| **New in `PricingTool.Engine`** | 6 | The hackathon host: `DailyCycleWorker`, `CostWatcherWorker`, `appsettings.Hackathon.json` |
| **Changed in the imported projects** | 25 | Per-category floors (`Core/Domain/CategoryBands.cs`, `PriceBandConfig.cs`, `MarginTargetService`, `Data/Entities/BandCategoryRule.cs`, `PricingRunCategoryRule.cs`, `PricingToolDbContext`, `DependencyInjection`, `BandConfigProvider`, `PricingRunOrchestrator`, `CategoryMapReader`); `RoundingService.IsOnGrid` (the run checker's off-grid test); the hackathon environment and its guard (Web and Engine `Program.cs`, `launchSettings.json`, `.csproj`); the employee email removed (`appsettings.json`, `PortaOptions.cs`, `User.cs`, `DbSeeder.cs`); the nav links and the inbox badge (`_Layout.cshtml`, `WaitingBadgeViewComponent`, `ViewModels.cs`). `git diff --name-only 9c977ca..HEAD -- PricingTool.Core PricingTool.Data PricingTool.Engine PricingTool.Web` lists them with the new files |
| **Tests (`PricingTool.Tests`)** | 28 | New test classes for every agent, limit, publisher path and backtest lever |
| **Docs and scripts** | 34 | `docs/hackathon-2026/` (plan, hours, prompts, the data dictionary, backtest results, failure cases, evidence), the export SQL, the laptop scripts, `GO-LIVE.md`, `GO-LIVE-LAPTOP.md` |

Reused: the calculator's engine (changed only in the files listed above), the band and rounding logic it already had, its Web pages (proposals, bands,
schedule, audit log) and its tests (692 at import, all still passing).

## 2. How to run it

1. Restore the hackathon database `PricingToolHackathon` on `(localdb)\mssqllocaldb` (the daily backups in
   `%LOCALAPPDATA%\PricingToolAgent\backups`; the data is not in git) and log Claude Code in once (`claude auth login`).
2. `dotnet build PricingTool.sln` and `dotnet test PricingTool.sln` (966 passed, 1 skipped: the opt-in real headless call; E17).
   The **Evidence** tab lists every figure of `numbers.json` with its label, method, sources (and when each was loaded) and
   where it is used (the one-pager, the financial sheet's cells, this file). Nine figures whose script is a SQL file
   (E12–E14, E20–E23, E25, E26) recompute live on the page: it runs that exact file as `evidence_reader`, a database user
   that can only read, and compares each cell with the stored value (31 Oct: all nine matched; E20 in 9 s).
3. The web app: `dotnet run --project PricingTool.Web --launch-profile hackathon` → http://localhost:5058 (the app and the
   engine refuse to start in any other environment)
   (Agent, Campaigns, Backtest, Exceptions tabs).
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
| **Run checker** | pass / hold / fix per flagged row of an engine run | `RunCheckRules` judge every row in code; a rule's block stays a block whatever the model says, anything the model escalates becomes a block, a fix is judged by the same rules | `Checker/RunChecker.cs:79`, `:132` |
| **Chat** | a reply, a brief, or a rule | briefs go through `CampaignLimits` and the planning loop; rules only tighten (exclude a brand or category, raise a campaign's minimum margin) and campaign planning enforces them; what-if figures come from code (`what_if_floor`) | `Chat/AgentChat.cs:228` |
| **Cost watcher** | nothing: no model | raises to the floor, holds a data-error jump, ends a discount above the shelf, judges a live campaign on its own minimum margin | `NopIntegration/CostWatch/CostWatcher.cs` |
| **Publisher** | nothing | with `Publisher:Target` = Sandbox (every config in this repo) the writer refuses any schema but `shop_sandbox_*`; every write is audited first so a rollback restores it exactly; a raise above the run's max(anchor, current) is refused again here, and a regular price someone changed since the run is never overwritten | `NopIntegration/PublisherOptions.cs:58`, `NopRegularPriceWriter.Decide` |

Every call is logged in `ai.AiCallLog` (model, tokens incl. cache, cost, duration, turns), and every data tool call in
`ai.AgentQueryLog`. The agents' runtime Claude calls: 47 calls, $5.72 in the live database and 5 calls, $1.03 in the test
database (E15). The two Claude Code sessions that built this used $272.70 since kickoff (both AI-judge dry runs included), $197.36 of it in the unattended
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

## 4. The numbers (framing A: what this build adds; estimated)

| ID | Lever | € a month, central (low / high) |
|---|---|---|
| E1 | Loss-sale block | 76 (76 / 198) |
| E2 | Per-category floors | 2 (0 / 4) |
| E3 | Cost reaction | 1,327 (148 / 1,771); the low case leaves out August, one cost event |
| E4 | Coverage MK/AL | 1,325 (560 / 1,654), at the calculator's Kosovo rate (E20) |
| E5 | Cadence | 396 (260 / 474) |
| E6 | Learning, as built | 0 (the plan's plain rule would have cost €1.0–1.3k in September) |
| E7 | Campaign planning (12 months of campaigns) | 783 (703 / 1,744: the uplift curve at half and twice its slope) |
| E24 | Raises reach the shelf (lever 8, the publisher) | 249 (249 / 2,830: October's pace, when two push sets overlapped) |
| **E8** | **Total** | **4,158 (1,996 / 8,675)** |

Levers 1, 2, 3 and 5 raise prices, and v2 doesn't raise a price a team deal holds (the publisher leaves it to a person).
Their gain on lines sold under a team deal (a product discount at a price no approval gave the SKU) is in the high case
only: €378 a month of their €2,179 central (17 %), most of it Gjirafa50's levers 1 and 3. Lever 8 is what v2's
publisher adds over the CSV import: in today's process 17–20 % of the units on SKUs an approval raised still sold at the
old price in Aug–Sep, 70–84 % in late October (E23), held by the calculator's own earlier push or a regular price never
raised (v2 removes both: one discount per SKU, ended with a regular-price raise) or by a team deal (€1,274 a month in
Aug–Sep, left to a person, not counted). It is scored in the backtest after the other levers, so a line another lever
scores counts once (E23's whole spans give €300). The stored run (#5) also reads each day's lines in sale-time order;
before, the database's order moved lever 3 by €35 a month.

Walk-forward on booked sales lines, each line counted by one lever, elasticity −1 central, −2 low, −0.5 high (assumed),
every lever on the calculator's era, Aug–Sep 2026: framing A's baseline is today's process. (A 12-month window, tried
after dry run 1, scored levers 1 and 3 against months before the calculator; dry run 2 caught it.) The sheet then leaves
out the holdout categories' share (about 11 % of the changed prices): the agents never change them.
Method per lever: [`docs/hackathon-2026/backtest-results.md`](docs/hackathon-2026/backtest-results.md); the Backtest tab
shows the stored run and its CSV. **This is far short of the €10k bar on its own.** Framing B (the rollout of the
calculator's test phase, E20: €4.4k a month measured with a same-weeks control (changed vs unchanged products in the
same weeks, one controlled run per KS layer); €10.4k measured before/after and €13.0k season-adjusted, neither
controlled) is a separate case, not added into these figures, with one dependency: lever 4 takes E20's rate (as a share
of Kosovo's gross profit) for the MK/AL months without a push, so framing A's mid and high cases rest partly on E20's
uncontrolled cases; with the controlled rate only, E4 is €560. E20's low and mid cases are recomputed from this repo's
data ([`evidence/e20-test-phase.sql`](docs/hackathon-2026/evidence/e20-test-phase.sql), run by `build-numbers.ps1`);
the high case's 2025 season adjustment is quoted from the main repo.

**Where the money comes from.** Most of framing A is automation, not the model's own choices: the loss block and the
cost reaction (levers 1 and 3: rules in code and the cost watcher), daily checked prices (5: the cycle writes them every
day, the team still pushes the file), raises that reach the shelf (8: the publisher) and coverage of every layer (4).
Most of levers 1, 3 and 5 are price raises; a
discount can't make one, so since prompt 07 the publisher writes a raise as the store's regular price (`TierPrice`), built
and tested on the sandbox, not live. On 31 Oct's Run weeks 99,197 of the two KS layers' 172,579 checked prices reached the
sandbox shelf at our price (57 %), 909 of them raises (1,167 raises were written; 258 sit under a cheaper deal); what
didn't was almost all a cheaper discount we didn't write (73,379), and 3 prices someone changed after the run. Those
discounts were mostly not team deals but the calculator's own earlier pushes (section 5, the caveat on raises). The model's own decisions are the per-category floors (lever 2: €2 a month in the backtest, €4 if a person supersedes the team deals, because
Alpha's rules move a floor one point a week) and the campaign plans (lever 7 replays the planner's per-SKU depth, which
code computes; the model chooses among the strategies). What the agents add so far is running the whole loop every day,
on every layer, without a person; how much their own judgment is worth is what the pilot's holdout measures. Framing B claims no past euro: the declaration said the calculator's measured past result is not claimed, and
framing B claims only the next 12 months of an automated system, using the past as evidence for the rate.

The [financial sheet](docs/hackathon-2026/Financial%20sheet.xlsx) carries both framings in three cases each (live
formulas; inputs typed from `numbers.json` with their IDs): year-1 net benefit, ROI, payback and the Track A score. The
year ramps up as the pilot ask does: built in month 1, a two-month pilot at an assumed quarter of the benefit, then full
scope (9.5 full-benefit months); the holdout categories' share (11 %, estimated from the exports) is left out. Costs: the attended hackathon hours at €50 (E16) plus an assumed hour of the person's
time inside unattended blocks, 160 assumed hours to production (including review of the AI-written code), 8 hours of
training and sign-off of the limits, the build sessions' AI usage (E18), 8 maintenance hours a month, half an hour of
exception review a week, and the agents' AI cost from the measured cost per call (E19) × an assumed monthly volume.
The Evidence (3) and Works (4) ratings in it are our own judgment, with the reason next to each.

## 5. What is mocked, injected or not finished

- **Publishing is to the sandbox only.** `Publisher:Target` is Sandbox in every `appsettings.Hackathon.json`; the writer
  then refuses any schema outside `shop_sandbox_*`. Nothing writes to a shop database. The sandbox holds the shop's
  discount tables and its regular prices (`TierPrice`, seeded from the 31 Oct KS copy), each with the live DDL. Since
  31 Oct a daily mirror step (`mirror-L<layer>`, before the publish) brings the live shop's other discounts, their product
  mappings and flags, and the regular prices the publisher hasn't written into it for the run's products, read-only from
  the replica, so each day's publish measures what would reach the live shelf (the October sets ended that night; without
  it the sandbox would describe a shop that no longer exists). Going live is configuration plus a DBA's grant
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
  - **The laptop start-up task** is written but not registered (the user's step); today the host is started by hand.
  - **Minutes saved per task** are not set, so the time sheet exports no rows yet.
  - **Campaign what-ifs** go through a brief and the planning loop; only the floor what-if has its own tool.
  - **To the live shop, the checked push file is still the path.** The daily cycle writes it, and since prompt 07 also
    publishes the same checked prices to the sandbox (`publish-L<layer>`, from the 1 Nov cycle on the restarted host). The
    whole loop on demand is **Run week** on the Agent tab (Alpha → engine → run checker → the checked prices to the
    sandbox → read-back; `RunWeekLoop`). Cuts go out as discounts, raises as the store's regular price
    (`NopRegularPriceWriter`); the cost watcher's raise to the floor goes the same way.
  - **The money ledger** has measured nothing yet: no pushed change has completed its 14-day window.
- **Known limitations** (several found by our own AI-judge dry runs, `docs/hackathon-2026/ai-judge/dry-run-1.md` and
  `dry-run-2.md`):
  - E20's high case (its 2025 season adjustment) is quoted from the main repo; its low and mid cases and E21 are
    recomputed here (`evidence/e20-test-phase.sql`, `evidence/e21-by-cause.sql`). E21 is not one leak the agents fix:
    of the €14.7k a month sold under the hand-set floor in Aug–Sep, €3.0k is allowed or not a price (dead stock,
    vouchers), €4.2k is GjirafaMall SKUs priced by Gjirafa50, and the rest sits in the lanes of levers 1 (€1.4k),
    3 (€1.1k) and 5 (€4.6k, which the backtest can't score) and team deals (€0.4k). E20's controlled
    low case is one run per KS layer, not a randomized test. Framing B's B0 is the non-AI calculator's effect: the declaration did not claim its past result, and whether its forward rate counts is
    the organisers' call.
  - E18 rests on the Claude Code session transcripts, which are not in the repo (they hold the whole build conversation).
  - Lever 1 assumes a blocked loss sale costs nothing else (no lost basket); levers 2, 5 and 6 rest on two months.
  - The kickoff rulings (reading the replica and the tool database, no API key) are recorded by us in
    `kickoff-plan.md` section 3, from the organisers' answers at the kickoff; their written confirmation would make
    them checkable.
  - A goal typed in the chat is the person's, binding on Nova, but the number stored is Nova's reading of the message,
    not one code parsed (`ai-judge/code-review-1.md`, finding 3).
  - Raises are written as the regular price on the sandbox only; going live needs the `TierPrice` grant and the shop's
    answer on its price cache and the `OldPrice` convention (`GO-LIVE.md`). A cheaper discount we didn't write still wins
    over our price (raise or cut) until a person supersedes it: on 31 Oct that held 43 % of the checked prices off the
    sandbox shelf. It was mostly not team deals: 96.5 % of the held prices carried a price one of the calculator's own
    approvals gave that SKU (its October pushes, which the team imports as discount sets, all ending by 4 Nov); over a
    third of GjirafaMall's (23.5 % of all) were app-only, which don't set the web price (written for the web since
    Prompt 10); real team deals held 1.5 % of the checked prices. With a go-live switch that supersedes only the
    calculator's own earlier pushes (single-product, carrying an approved price), 171,108 of 174,087 checked prices
    (98.3 %) reached the sandbox shelf (measured 31 Oct on runs #1109 and #1107, then rolled back); the rest were team
    deals (2,969) and 10 prices changed since the run. The switch stays off until the commercial owner decides.
    Today's process stacks its own pushes: approval #37's set was imported on 20 Oct while #32's stayed live to 31 Oct,
    and the shop applies the cheaper, so #37's raises didn't reach the shelf. That is why v2 keeps one discount per SKU,
    updated in place, and ends its own in the same transaction as a regular-price raise.
  - With `Publisher:Target` = Sandbox the guard is on the schema name (`shop_sandbox_*`), not the server.
  - The hackathon's 8,000-request bar: the agents make about 290 calls a month by design (one per run or pass, never
    one per product), and no call is made to raise a count.

## 6. Rule compliance

| Rule | Proof |
|---|---|
| Read-only data access; no shop writes | The replica connection exists only in user-secrets (`ConnectionStrings:SourceReadOnly`); the agents' `query_data` runs SELECT-only on `agent.*` views with a 30 s timeout and 500-row cap (`QuerySqlGuard`, tested); the publisher writes only `shop_sandbox_*` |
| Every copied dataset is logged | `hk.DataSources`: 37 rows (source, time, row count, the SQL file in `docs/hackathon-2026/exports/`) |
| No personal data | Sales lines carry no order, customer, email, name or address; the AI call log redacts emails. The imported calculator's default admin was an employee's work email: removed from this tree on 31 Oct (it remains in the import commit) |
| No live system written | The replica and a one-time read-only copy of the tool database are read, both allowed at the kickoff. On 31 Oct a web start with the wrong launch profile (Development) migrated and seeded the main repo's *local* development database `PricingTool` on this laptop. Five minutes later, after a backup, its schema changes were reverted (every object created at 00:20:58–59 dropped). The demo data it seeded is still there (one demo run #40010 with its 360 proposals, one day of snapshots and KPIs, and 360 updated dead-stock states): the backup was taken after the start, so the updated rows' earlier values can't be restored, and removing only the inserted rows would leave that database inconsistent; the user decides (`docs/hackathon-2026/ai-judge/dry-run-1.md`). The web app and the engine now refuse to start in any environment but Hackathon |
| No secrets in the tree | `git grep -i -E "password|pwd=|api[_-]?key|secret"` finds only code that reads secrets from configuration and docs that say where they live |
| No AI-reviewer text | None anywhere; the plan forbids it |
| Hours as a cost line | `docs/hackathon-2026/hours.md`: 3.9 attended hours and 24 unattended blocks (E16); unattended blocks count as AI cost only, $197.36 API-equivalent (E18) |

## 7. Tests

`dotnet test PricingTool.sln`: 966 passed, 1 skipped (31 Oct, 10:05; three runs in a row, 13–14 s each). The twelve failure and unusual cases the plan names
(API down, timeout, malformed output, thin data, missing cost, kill switch, auto-rollback, unreachable campaign goal,
overlapping discount, injected cost rise, data-error cost jump, bad `query_data`) map to their tests in
[`docs/hackathon-2026/failure-cases.md`](docs/hackathon-2026/failure-cases.md).
