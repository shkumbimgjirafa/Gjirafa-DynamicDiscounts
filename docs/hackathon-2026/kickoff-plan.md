# Gjirafa AI Hackathon 2026: kickoff plan (for Claude)

Written 2026-10-27, before the event. **This is a plan, not code.** No solution code may be written
before **Fri 30 Oct 16:30**. A Claude session that opens this file before then may only help with
the "Before Friday" list in section 3.

Sources: "1. Proposal (v3)" (Drive `1KLpp0_w9oulfdaaTkUbi15ivi3RpjnBG3xgWs1k5fb8`) and
"2. Pillar challenges" (`1nyXrhgFBH-GgJIjqxChLzuGd46KYKoikvO0E-u3hXf0`). Both were re-read on
2026-10-27 and were unchanged since 10-26 18:05. **Re-read both at kickoff**, because the 17 cards
were still headings only.

---

## 1. The rules that shape every decision

| Rule | What it means for us |
|---|---|
| No solution code before Fri 16:30; existing code allowed if declared | A **new repo, created at kickoff** in its own folder (section 5, "Repo setup"). Its history starts on Friday: the declared calculator comes in as one labelled import commit, and every commit after it is hackathon work. The existing calculator is declared (section 2). |
| AI must do part of the work itself; "an ordinary app written with AI does not count" | The AI must **decide** things (floors, targets, pass/hold on prices), not only explain them. |
| No live systems touched; data from the warehouse or company exports; say where each file comes from | Nothing writes to a prod DB or the NOP replica. *(Kickoff ruling, 30 Oct: we may read the replica with our own read-only connection; section 3.)* All data comes from labelled exports or the warehouse, loaded into a separate LocalDB. The discount publisher writes only to a local sandbox copy of the shop's discount tables; going live with it is the pilot, after the event. |
| No customer personal data, no per-employee data | Sales lines carry SKU, date, qty, net and cost only. No order ids, emails, names or addresses. |
| Show what is mocked or not yet working | A visible banner shows the data source and date, and every injected test case is labelled. |
| No passwords or keys in code, slides or screenshots | The API key and warehouse credentials live in env vars or user-secrets. The new repo has no `k8s/` and no `.env` at all. Never screen-share the main repo's `k8s/*.yaml` or `.env`. |
| Teams of 1–2; others may explain data, not write the solution or the financial case | Only the team builds. Claude is an AI tool, which is encouraged. |
| Submission by **Mon 2 Nov 08:00**: prototype, one-page summary, financial sheet (Track A formula), 2-minute backup video | These are the hard deliverables (section 8). |
| Finals: 4 min pitch + live demo, 4 min questions | Demo script in section 7. |

**Track A scoring (100 points):**
- Net benefit, year 1: 40 points. A rating of 5 needs above €100k; 4 needs €40–100k.
- ROI: 10 points.
- Payback: 10 points.
- Evidence: 20 points. 3 = real baseline and tested on past data. 5 = measured live in the demo on real data, every input sourced.
- Works: 20 points. 3 = works end to end on real data, with visible errors. 5 = normal, unusual and failure cases all work. **Below 3 = no prize.**

**Money rules:**
- Count margin, not revenue.
- Every person-hour costs €50, including our hackathon hours.
- Hours saved are capacity, not money.
- Label every input as measured, estimated or assumed, with its source. Finance strikes out any line it can't trace.
- **The claim** (the financial sheet, section 8): the full system, told as test phase → rollout.
  - The calculator (v0.5, Jul–Oct 2026) **was the test phase**: prices it calculated went live by hand on a few dates in Kosovo, and the effect was measured (E20: €10.4k a month, before and after each approval, booked sales lines). v2 is the **rollout** of the tested approach, automated, weekly and on every layer. Name the design honestly: "a test phase measured before/after against the same weeks of 2025 [and changed vs unchanged SKUs in the same weeks, if that comparison is run]". Never call it a randomized A/B test; there was no random control group, and a reviewer will check.
  - *(31 Oct: the record shows four October approvals and pushes on MK/AL in some months, so "only in KS" no longer holds.)*
  - Background: the calculator is an unsupported prototype that only earns when one person pushes prices by hand, irregularly and only in KS. Without v2 nothing secures that profit. v2 turns it into a system that runs and improves on its own every week, on every layer. So the benefit is everything the automated system earns in the next 12 months, forecast from the measured rate.

## 2. What is declared (submitted in the registration form)

**Existing: the pricing calculator (prototype), running internally since July 2026.**
- It's a .NET 8 app on SQL Server (`PricingTool.Core`, `.Data`, `.Engine`, `.Web`) with no AI model in it.
- It reads shop data and calculates proposed prices with rules and statistics.
- It isn't connected to the shop's pricing, so nothing it calculates goes live by itself.
- Floors and targets are set by hand. People review its proposals, export them and apply them manually.
- Its measured effect: about €10k extra gross profit a month in Aug–Sep 2026 (Aug €11,184, Sep €10,079, KS only, before/after on applied batches). This is **not claimed** as hackathon benefit.

**Reused:** the calculation engine, the database layer with its history, the Bands page, and the before/after profit measurement.

**New (the build), as promised in the form:**
1. **AI floor and target agent.** Decides each band's margin floor and target per main category, writes a reason for each, and checks its own previous changes.
2. **Agent data tools.** Read-only queries for sales and margin by band and category, cost changes, stock and stock age, and past outcomes. Plus `query_data`: the agents write their own read-only queries against curated views, with every query logged.
3. **Learning loop.** Measures what each applied change did to profit and units, and uses that in the next decision.
4. **AI run checker.** Rule flags first, then the AI decides pass, hold or fix per flagged row, with a reason.
5. **Approval inbox → exceptions inbox.** In the default autonomous mode, a person sees only what the agent held, paused or rolled back. An "approve-first" mode per layer keeps one-click approval for anyone who wants it.
6. **Safety limits and audit log.** Hard limits, auto-pause and rollback, a kill switch, and every AI decision logged with its inputs and reasoning. A 15-minute cost watcher raises prices that a cost rise pushed below the floor, without waiting for the next run.
7. **Backtest.** Replays the agent on Aug–Sep data against the hand-set floors. This is the evidence for the financial case.
8. **Agent chat, including the weekly brief.** An Agent tab where you talk to it:
   - ask why it made a decision
   - ask "what if" (answered with the backtest)
   - give instructions in plain language, which become rules enforced in code

   Every Monday its first message is the weekly summary.
9. **Discount publisher, go-live ready.** Writes the final prices into the shop's NOP `Discount` tables **the way the NOP discount module does**, with no export step:
   - create the discount, apply it to the product, set the product's discount flags, edit, expire, and clear the cache
   - time-boxed campaigns
   - audit and rollback
   - round-trip verification of the live price

   Going live is a config switch (`GO-LIVE.md`), never a code change. During the hackathon it writes to a sandbox with the exact live schema.
10. **Campaign agent (a second AI agent).** It owns strategy:
    - watches sales, profit and revenue trends and the results of every change and campaign
    - keeps the calendar of main campaigns
    - plans main campaigns and proposes small mini-campaigns (per category, brand or anything else)
    - sets each campaign's goal (e.g. "Laptops, 3 days next week, €X gross profit")

    It sends that goal as a **brief** to the pricing agent (item 1). The pricing agent works out the discounts, margins and floors that reach it, and answers whether the goal is reachable within the safety limits. The two agents' exchange is visible in the app.

**Two agents, one division of labour:** the campaign agent decides *what to achieve and when*; the
pricing agent decides *how*, with the calculations. Both use the same safety limits, publisher and
audit log. You talk to them in the chat; the campaign agent is your main point of contact.

**The goal: a pricing loop that runs on its own.** The agent sets the floors, the engine prices,
the checker reviews, and the publisher creates the discounts in the shop. A person no longer approves
bands or exports prices. They watch the exceptions and hold the kill switch. Safety comes from limits
enforced in code, automatic rollback and a holdout group (section 5), not from someone clicking approve.

Note on the registration text: it says "with a person approving its changes". The build keeps that as
the approve-first mode, but the pitch and demo run in **autonomous** mode. `HACKATHON.md` explains
the difference in one line, so a reviewer sees an extension of the registered idea, not a contradiction.

Everything in this list must work, or be shown as not working, in the demo. If something gets cut,
say so in the demo and the one-pager. Don't hide it.

**Also existing; declare it if the build touches it:**
- `PriceChangeOutcome` / `OutcomeEvaluationService`: per-SKU win/neutral/backfire grading.
- `MarginTargetService`: the guardrail that nudges each band toward its target.
- `BandMatchService` / `BandFloorMirrorService`: copy KS floors and targets to MK/AL.
- `TargetMarginImpact`.

Note: `README.md` is titled "Gjirafa AI Dynamic Pricing Tool — v1", while the declaration says
"no AI model". Either retitle it in the hackathon repo or be ready to explain that the name was
aspirational and the engine is rules plus statistics.

## 3. Before Friday (the user does these; no code)

**Kickoff answers (Fri 30 Oct, recorded as they come in):**
- **NOP replica:** allowed. We read it with our own read-only connection (connection string in
  user-secrets only). This replaces "exports only" for everything that lives on the replica: the
  engine's source dataset, sales lines, WMS check-ins, the discount schema and the active discounts.
  Writes to any shop DB stay forbidden; the publisher still writes only to `shop_sandbox`.
- **Tool DB:** reading is allowed. Copied once, read-only, for the KS layers into
  `PricingToolHackathon` (`docs/hackathon-2026/exports/Copy-ToolDbKs.ps1`); the app itself never
  holds that credential.
- **API key: not needed (Mergim's ruling, via Mergca).** The app's own AI calls go through the
  team's Claude subscription: Claude Code headless (`claude -p --output-format json`), batched (one
  call per run, not per row), arithmetic in our code, strict JSON validated with one retry then
  block. Those calls count toward the **8,000 requests of the 10% bar** (genuine calls only). If the
  usage limit is hit, contact Mergim and he raises it. Mergca offered to review the first check's
  call, rules file and sample JSON: send them when the run checker first runs.
- **The agent writes its own row in the time sheet for every check it does;** manual rows count as
  the wrong path. The sheet is the organisers' `docs/hackathon-2026/Time-sheet-template-hours-saved.xlsx`
  (sheet "Time sheet", rows from row 7): Date · Who used it (Gjirafa email) · What: the task · Time
  BEFORE without AI (minutes) · Time AFTER with AI (minutes) · Minutes saved (formula) · Proof (link or
  note) · Comment. It covers **2 Nov – 2 Dec** with a **30-hours-saved** goal; "Jarvis connects your
  solution on Monday"; Mergca checks every row daily against the solution's own usage, and inflated or
  invented numbers remove the entry. Time BEFORE is the one number we set, once per task type,
  measured honestly. See "Time-sheet log" in section 5.
- The baseline: no official ruling yet. **Mergca (Mergim's AI), 31 Oct evening, labelled "my reading of the rules, not an
  official ruling"** (Mergim's official ruling was requested the same evening, for every entry built on declared code):
  - The baseline is what would happen in the next 12 months without the weekend build: "adopt the automated system vs.
    don't adopt it".
  - "Today's process carries on" is right only if someone will keep running it by hand; then only what the build adds on
    top counts. The full measured rate for 12 months is right only if the manual run would stop without the build, which
    needs a source (a one-line confirmation from the e-commerce owner); without it, it is assumed.
  - The measured version: without the build = the tested rate × how often the manual engine was actually live ×
    pass-through; the benefit = the automated system's 12 months minus that line, plus the new levers. (Our note: the gap
    between the manual engine as it ran and the automated one is already counted by levers 5 (cadence) and 8 (raises
    that reach the shelf, from E23's pass-through); MK/AL coverage (lever 4) is not in the claim. B0 was measured on the manual process as it ran.)
  - Past months stay unclaimed, as the registration says; Aug–Sep is only evidence for the rate.
  - The claim stays the full system's next 12 months (the user's decision, 31 Oct), with "the August–October test ends
    without the build" stated by the user, who ran it; the e-commerce owner's line or Mergim's ruling would add a second source.

**The 10% bar (announced Fri 30 Oct; separate from the prizes, decided Wed 2 Dec).** A permanent
10% raise and the "AI Pioneer" title for everyone who clears it; solo = 10%; no cap on how many.
The 30 days run from Monday 2 Nov. All five:
1. **Live on Monday:** runs on real company data, and the AI does part of the work itself.
2. **30 hours saved in 30 days,** filled in by the AI, not by hand (the time sheet above).
3. **8,000 Claude requests in 30 days:** real AI work, counted in the company usage report.
4. **Worth 3× the raise (this one decides):** in the 30 days the solution brings at least three times
   the raise's monthly cost in money (extra margin, costs avoided, cash recovered). Hours don't count here.
5. **Mergca sees it directly:** on Monday Jarvis adds a usage tracker to the code (every run reported)
   and Mergca reads the code. Inflated numbers, **mock data**, or someone else building it = out.
Private progress notes on day 10 and day 20; one appeal.

**What it changes for the build:**
- **Built to run for 30 days, not only to demo.** Daily: refresh the `hk.*` data from the replica
  (incremental, inside the app, not the one-off prep scripts), engine runs, Alpha's pass, the run
  checker, the cost watcher, Nova's weekly pass, and the time-sheet rows, all on their own.
- **Real prices must reach the shop** for step 4: AI decisions that only land in the sandbox earn
  nothing. Which path (direct publisher with a canary vs today's export and push) is the user's call.
- **A money ledger for step 4:** every applied action records its expected € and, after N days, its
  measured € against the holdout (`OutcomeTracker`), plus the cost watcher's losses avoided, each
  with its evidence. That ledger is what Mergca checks, so it must be conservative and traceable.
- **No mock data anywhere in the live database.** `[INJECTED-TEST]` rows live only in the test
  database and the sandbox.
- **Requests:** only genuine calls; the app's batched calls plus the user's own building count.

**Live path (user's decision, Fri 30 Oct): this laptop, for the 30 days.** The hackathon app keeps
running here on LocalDB `PricingToolHackathon`, reading the replica daily; **prices reach the shop
through today's export and push** (the existing CSV export, pushed by the team as now). The
go-live-ready direct publisher stays built and tested against the sandbox; switching it on is a
later step, not part of the bar. What that needs (plan section 5, "Live operation on the laptop"):
- a daily cycle that starts on its own (and catches up after the laptop was asleep or off);
- incremental refresh of the `hk.*` data, and retention so LocalDB stays under 10 GB for 30 days;
- the AI-checked export for the team's push, from this app instead of the prod tool, with the
  holdout categories left out, and the pushed sets recorded as applied;
- the money ledger and the time-sheet rows, both written by the app;
- a daily database backup.

The user expects the open questions (data rules, API key, warehouse access) to be answered at the
**kickoff on Fri 16:00**. Collect the answers there before the first data step. The schedule is
built so nothing before Fri 19:00 needs the API key, and the data step has an export fallback if
the warehouse is missing anything.

- [x] **Registered on 2026-10-27.** Track A, card #4 E-commerce business.
- [ ] **Post the questions in the Chat space "Gjirafa AI Hackathon 2026"** and record Mergca's answers here:
  - Can a declared, already-deployed tool be extended, with only the weekend work scored?
  - Can the calculator's measured result be used as evidence?
  - **Baseline for net benefit:** is it measured against the status quo, where a prototype is run by hand by one person with no supported process, so that the automated system's whole forecast counts? This decides the 60 money points, so ask it word for word.
  - Does reading the read-only NOP replica, or the tool's own prod DB, count as "live"?
  - Can data exports be prepared before Friday?
  - Which card is this, and who grants warehouse access?
- [ ] **Anthropic API key for the prototype.** The company Claude subscription covers Claude Code for building. The app's own AI calls need an API key. Ask who provides it, and count its usage in the running cost.
- [ ] **Warehouse access** from the e-commerce pillar lead. Check whether it holds the inputs in section 4. Whatever it lacks becomes an export.
- [ ] **Teammate or solo.** Decide now. Hours × €50 go into the cost.
- [ ] **Laptop check:**
  - dotnet 8 runs with `$env:PATH="C:\Program Files\dotnet;$env:PATH"`
  - LocalDB starts
  - the app runs on http://localhost:5058
  - at least 15 GB of free disk (LocalDB caps each database at 10 GB)
- [ ] **Secrets:** the main repo carries the prod DB password in `k8s/*.yaml` and in its history. That's why the hackathon gets its own new repo, with no k8s manifests, no `.env` and no pipeline (section 5). The submission therefore has no prod credentials anywhere, including its history. Rotating that password and moving it to a k8s Secret is still worth doing for the main repo, but it's no longer needed for the hackathon.

## 4. Data plan (no live systems during the build)

All of it goes into a **new LocalDB database `PricingToolHackathon`**, separate from the normal local
`PricingTool` database because of the 10 GB cap. KS layers only: **L1 GjirafaMall KS, L4 Gjirafa50 KS**.
Every table gets a row in `hk.DataSources` (file, extracted-at UTC, origin, row count, who
extracted it). That table is shown in the app footer, which covers "say where each file comes from".

| Input | Used for | Source (order of preference) | Approx. size |
|---|---|---|---|
| Daily pricing dataset per layer (the engine's `SnapshotRow` input) | Running the existing engine locally on real data | Export of the engine's dataset query, one run per layer. Origin: `SqlSourceDataReader` (stored proc / InlineQuery) | ~350k + ~110k rows |
| Sales lines May–Oct 2026, KS Mall + Gj50: SKU, date, qty, NetoPrice, Cogs, Margin, status, platform/company. **No order or customer ids** | Band × category performance, learning loop, backtest | Warehouse if it has `SR_ProductsData`; else an export. Apply the known filters: statuses 10/20/30, exclude LOADTEST orders, exclude outlets, Apple/Starlink | 1–3M rows |
| Price bands + `PricingRunBandTargets` (floor/target history) | Hand-set floors to compare against | Export from the tool DB | small |
| `Approvals` + `ApprovalItems` (KS) | Past outcomes, and the €21.3k evidence | Export from the tool DB | ~1.7M items |
| `SkuElasticity` | Volume response in the backtest | Export from the tool DB | small |
| Discount schema (DDL of `Discount`, `Discount_AppliedToProducts`, the `Product` discount columns) for both store DBs, plus the **currently active** discounts and product mappings for the demo layers | Exact-schema `shop_sandbox`, schema contract test, and supersede, overlap and round-trip on real state | Schema snapshot from the store DBs (metadata only), plus an export of active `DiscountApplyTypeId = 2` rows for KS | schema: tiny; active KS discounts ≈ 150k rows |
| SKU → main category | Per-category floors | Warehouse, or an export using the `gjirafa-categories` skill. Mall groups: Kozmetikë, Teknologji, Shtëpi, Sport, Vegla Pune, Fëmijë, Others | ~460k rows |

**Export rules:**
- One script per input, saved in `docs/hackathon-2026/exports/`. These are data-prep scripts, not part of the solution.
- Run them once, record the time and source, then disconnect.
- **Exports run after 16:30 Friday unless Mergca said prep is fine.**
- After the exports, no session may use `Invoke-ProdSql`, the k8s connection strings, or `.env` source credentials.

**Sanity check after loading:** August GP of the approved SKUs computed from the exported sales lines should match the
`Approvals.Profit30*` windows to within a few percent. If it doesn't, the export is wrong. Fix it before building on it.

## 5. Architecture of the new code

**Repo setup at kickoff (Fri 16:30).**
- **New folder `C:\Projects\Gjirafa Hackathon Pricing Agent`, with `git init`.** The Friday Claude session is opened there, not in the main repo.
- **Commit 1, docs only:** this plan, `DECLARATION.md` (the registration text), `hours.md`, `.gitignore`, and the existing integration design `Dynamic Discounts Platform integration.docx` (a design doc, not business data; the publisher follows it).
- **Commit 2, the declared existing code:** `git archive` of the main repo's `main` at a recorded SHA, copying only `PricingTool.Core`, `.Data`, `.Engine`, `.Web`, `.Tests`, `PricingTool.sln`, `.config/` (dotnet-ef tool manifest) and `README.md`. Message: `Import declared existing code: pricing calculator v0.5 from Gjirafa Dynamic Discounts @ <sha> (not hackathon work)`. The SHA lets anyone check the import against the original.
- **Never copied:** `k8s/` (prod password), `.env`, `azure-pipelines.yml` (deploys to prod), `Dockerfile`, `docs/` (business reports), `scripts/`, `*.csv`, `*.log`, `PricingTool.Web/exports/`, `promt/`.
- **Credentials:** hackathon warehouse and API credentials go in `dotnet user-secrets` or env vars, never in a tracked file. The app DB is LocalDB `PricingToolHackathon`.
- **`.gitignore`:** `data/` (exported company data stays local, never committed), `.env`, `*.bak`, `exports/`.
- **Remote:** whatever the organisers name at kickoff. **Never add the main repo's `origin`.**
- **After the hackathon:** `git diff <import-sha>..HEAD` is exactly the hackathon work. It ports back to the main repo as a patch, mostly the new `PricingTool.Agent` project plus a few engine and web changes.

All new code goes in a **new project `PricingTool.Agent`** (class library, references Core + Data),
plus a new controller and views in Web and a new EF migration. That keeps "new vs existing"
visible in the diff.

**Environment and config**
- **`appsettings.Hackathon.json`:**
  - `PricingToolDb` → `(localdb)\mssqllocaldb;Database=PricingToolHackathon`
  - `PricingEngine:UseDemoData=false`
  - a new `PricingEngine:SourceMode=LocalExport`
  - `Agent:*` options: models, limits, enabled flag
- **Run with** `ASPNETCORE_ENVIRONMENT=Hackathon`.
- **API key** from env var `ANTHROPIC_API_KEY` or user-secrets, never committed.

**Local data readers** (register them in `DependencyInjection`; `SourceMode=LocalExport` selects them):
- `LocalSourceDataReader : ISourceDataReader`, reading `hk.SourceDataset`.
- `LocalSalesReportProfitReader : ISalesReportProfitReader`, reading `hk.SalesLines`.
- `LocalSkuSalesHistoryReader`.
- Elasticity fitting is skipped. Use the exported `SkuElasticity`.

**Claude client** (changed at kickoff: no API key, Claude Code headless through the subscription).
- `ClaudeCodeClient` runs `claude -p --output-format json` as a subprocess, prompt on stdin, from a runtime folder outside the repo, isolated from the repo's and the user's own instructions (see `CLAUDE.md`). `--json-schema` shapes the answer; `--tools ""` removes built-in tools; our read-only data tools and `query_data` reach it as an MCP server (`--strict-mcp-config --mcp-config`).
- Batched: one call per layer pass (Alpha), per run (run checker), per weekly pass or chat message (Nova). Our code does the arithmetic and sends only the rows that need judgement.
- Structured JSON validated in code; one retry, then hold or block.
- Timeouts and retries.
- **Log every call:** model, input/output/cached tokens, latency and cost. That log is the *measured* AI cost for the financial sheet.
- **Models:**
  - floor/target agent: `claude-opus-5-5` (few calls, judgment-heavy)
  - run checker: `claude-sonnet-5-5`, or `claude-haiku-5-5` if quality holds (many rows)
  - confirm current ids and prices in the skill

**The two agents' names (user, 2026-10-30): Alpha and Nova.**
- **Alpha · pricing agent** (`FloorTargetAgent` + `PlanCampaign`). In finance, alpha is the return above the benchmark, which is exactly its job: profit above the holdout.
- **Nova · campaign agent** (`CampaignAgent`). A nova is a star that flares bright for a short time and settles back, which is what a campaign is.
- Demo line: "Alpha earns the margin every day; Nova lights up the campaigns."
- In the UI the role always shows next to the name ("Alpha · pricing agent"); in the chat they're `@alpha` and `@nova`. Names are display config only: class names stay functional, and the system prompts don't depend on them.

**Agent data tools.** Read-only, each returning compact JSON:
- `get_band_performance(layer, band, category, days)`: units, net, GP, realized margin, share sold below floor, share at floor.
- `get_cost_changes(layer, band, category)`: SKUs whose cost moved more than 5% recently.
- `get_stock_health(layer, band, category)`: stock value, dead-stock share, oldest-unit age.
- `get_past_decisions(layer, band, category)`: earlier agent decisions and their measured outcomes (the learning loop).
- `get_elasticity_summary(layer, band, category)`: fitted elasticities and how many SKUs have a usable fit.
- `get_current_config(layer)`: today's floors and targets.

**`query_data(sql)`: the agents' own read-only queries** (added 2026-10-30). Fixed tools only answer
questions we thought of in advance; most of what made the hand-set Për ty plan land (Gj50-listed SKUs
at 37% of Mall revenue, discount optimization left on, the real engine cutting shallower than the
model, Morele's 9 Oct cost rise) came from questions nobody planned. Both agents and the chat get a
tool that runs their own SELECT and returns the rows, so they can investigate the way an analyst does.
- **Curated views only, never raw tables.** The views carry the definitions we learned the hard way, so the agent can't get them wrong:
  - `agent.SalesLines`: booked COGS for past windows; cancelled and load-test orders removed; outlet (`[OUTLET` name tag), Apple and Starlink excluded
  - `agent.Products`: platform, category, brand, and the "Gj50-listed on Mall" flag
  - `agent.CampaignHistory`: the 15 KS campaign windows
  - `agent.CostChanges`: cost moves since the previous dataset
  - `agent.EngineProposals`: the latest engine dry run, with reason codes and flags
- **Guardrails in code:** a read-only login granted SELECT on the `agent` schema only; one statement per call, SELECT only (parsed and rejected otherwise); 30-second timeout; at most 500 rows returned; a SQL error goes back to the model as the tool result so it can fix its query.
- **Logged and shown:** every query, its row count and duration are stored and listed on the Agent tab under "what it looked at", so each decision shows the evidence it read.
- **Investigate, never decide.** Decisions still go only through `PlanCampaign`, `FloorTargetAgent` output and `SafetyLimits`.
- **The data dictionary** (views, columns, definitions, known traps) sits in the agents' system prompt, which is prompt-cached.
- During the hackathon it runs against `PricingToolHackathon`. At go-live it points at a reporting replica, never the shop DB.

**`FloorTargetAgent`.** One weekly pass per layer:
1. For each band × main category, the agent reads its tools and returns `{action: raise|lower|keep, newFloorPct, newTargetPct, confidence, reason, evidenceRefs}`.
2. Every decision passes through `SafetyLimits` and is stored as an `AgentDecision`.
   - **Autonomous mode (default):** within limits, it's applied straight away (status Applied, audit-logged). Outside limits, it's held to the exceptions inbox.
   - **Approve-first mode:** everything waits for one click.

**Autonomy switch, per layer:** Off / Approve-first / Autonomous, stored in `ToolSettings` and shown on the
agent page. The demo runs Gjirafa50 KS in Autonomous.

**`SafetyLimits`.** Code, not prompt, and it can't be overridden by the model. Because nobody approves
each change, these limits are what make autonomy safe:
- **Floors and targets:** at most ±1pp per week (configurable). The floor never goes below the hard minimum per layer, and the target is never below the floor.
- **Publishing caps:**
  - at most X% of a layer's SKUs re-priced per day
  - at most Y% price move per SKU per week
  - at most Z points of blended-margin change per band per week

  Anything over a cap waits for the next day, or goes to the exceptions inbox.
- **Never below cost** outside the dead-stock lane (the existing rule), and never above the anchor (the existing firm rule).
- **Auto-pause and rollback.** If a category's GP/day or units fall more than a set % below its seasonal expectation after a change, the agent rolls the change back, pauses that category and reports it.
- **Holdout:** a few categories per layer stay on hand-set floors and are never touched. They are the control group that turns the gain from estimated into measured.
- Thin data (fewer than N units in 30 days) forces `keep`.
- Malformed or out-of-range output is rejected and logged.
- An API failure keeps the current values and shows in the exceptions inbox. The loop never publishes on partial results.
- `ToolSetting Agent.Enabled` is the kill switch. It stops the agent and the publisher at once.

**Discount publishing: writes NOP discounts the way the NOP discount module does.** Requirement (user,
2026-10-29): the tool itself creates and updates the shop's discounts in the `Discount` tables, exactly
as nopCommerce's discount module does when an admin creates a product discount. Going live must be
**a config change, never a code change**; the code that runs against the sandbox on the weekend is
the code that runs against the shop.

This replaces the staging-table hand-off in the June integration draft
(`docs/Dynamic Discounts Platform integration.docx`). That draft raised two concerns, the nopCommerce
cache and write access to live tables. Both are handled explicitly below (cache invalidator,
round-trip check, least-privilege login, caps, audit and rollback). `HACKATHON.md` says so in one line.

*`NopDiscountWriter` (project `PricingTool.NopIntegration`), behind `IPricePushService`. What the module does, step by step:*
1. **Create the discount: insert into `Discount`.** Field values copied from the team's live pushes (checked 2026-10-28 on both store DBs):
   - `StoreId` per layer (KS 2, MK 1, AL 3)
   - `DiscountTypeId = 2` (assigned to products), `UsePercentage = 0`, `DiscountPercentage = 0`, `DiscountAmount` = the final price (it is the price, not an amount off)
   - `DiscountApplyTypeId = 2` (public), `DiscountPlatformTypeId = 0` (web + app) or `2` (app-only)
   - `RequiresCouponCode = 0`, `CouponCode = NULL`, `IsCumulative = 0`, `DiscountLimitationId = 0`, `LimitationTimes = 0`, `AppliedToSubCategories = 0`, `IsVendorDiscount = 0`; `DisplayAvailabilityTime = 0` (GjirafaMall only)
   - `MaximumDiscountAmount` / `MaximumDiscountedQuantity` = NULL
   - `StartDateUtc` / `EndDateUtc`
   - `Name`: `ai-price-<layer>-<sku>` for regular prices, `ai-campaign-<id>-<sku>` for campaigns
   - `AdminComment`: batch id + run id, so every row traces back to the decision that made it
2. **Apply it to the product: insert into `Discount_AppliedToProducts`** (`Discount_Id`, `Product_Id`), the module's "Applied to products" step.
3. **Flag the product.** Set `Product.HasDiscountsApplied = 1` and add the layer's store to `Product.HasDiscountsAppliedStoreIds`, a colon-separated, sorted list of store ids, nvarchar(40) (live values look like `1:2:3`, `2`, `2:3`). Without these flags nopCommerce never looks at the product's discounts. Never remove another store's id.
4. **Edit (update in place).** For regular prices, each SKU has **one `ai-price` discount per store**, and a price change updates its `DiscountAmount`, like the module's Edit. Today's pushes insert one row per product per push (GjirafaMall `Discount` is at 3.2M rows); daily publishing must not add 300k rows a day.
5. **Expire.** Set `EndDateUtc`. Never delete: order history and `DiscountUsageHistory` refer to discount ids.
6. **Clear the cache.** The module clears nopCommerce's discount and price cache through its save events; a writer outside the store app has to trigger it. `IShopCacheInvalidator` has three modes, chosen in config:
   - `HttpEndpoint`: call the store's cache-clear endpoint (URL and token from secrets)
   - `WaitForExpiry`: wait the shop's configured cache time
   - `None`: sandbox

   Either way, the **round-trip check** below confirms when the new price is really live. Before go-live, ask the platform team how today's monthly script pushes become visible, and use the same mechanism.

*Running it safely on a live 3.2M-row table:*
- **Set-based and chunked.** Bulk-load the batch into a temp table, then INSERT … OUTPUT (to capture the new `Discount.Id` per SKU) → mapping insert → flag update, in chunks of about 5k SKUs per transaction. `Discount` carries two wide covering indexes plus the clustered key (`PK_Discount`, `IX_Discount_Active_Store_Coupon`, `IX_Discount_CouponCode_Dates_Active`), so short transactions keep locks short. Publish right after the nightly engine run (03:00), not at peak.
- **Idempotent.** The `Name` encodes layer + SKU (or campaign + SKU), so re-running a batch finds its own rows and updates instead of duplicating.
- **Audit and rollback.** Before every change, write the previous state of each touched row (amount, dates, product flags) to our `PublishAudit` table. Rollback restores it exactly, and an automatic rollback reuses the same path.
- **Rolling expiry as a dead man's switch.** `ai-price` discounts get `EndDateUtc` = today + N days (e.g. 7), renewed each run. If the system ever stops, its prices fall back to the shelf price within N days.
- **Campaigns are time-boxed** with their own start and end, and expire on their own.
- **Overlap.** The lowest active public deal wins, so before publishing check the SKU for a cheaper active discount we didn't create.
  - Campaign discounts we didn't create are never touched.
  - The team's *regular* monthly discounts on the SKU are end-dated only when `SupersedeTeamRegularDiscounts` is on (default off; the commercial owner decides before go-live).
- **Least-privilege login per store DB:**
  - INSERT/UPDATE on `Discount`
  - INSERT on `Discount_AppliedToProducts`
  - UPDATE on `Product(HasDiscountsApplied, HasDiscountsAppliedStoreIds)` only, as a column-level grant
  - SELECT for verification
  - no DELETE, nothing else
- **Round-trip verification** after every batch: re-run the tool's current-price query (the same one the engine uses) for the published SKUs and assert CurrentPrice = the published price. Mismatches (a cheaper deal winning, an unresolved product, cache not refreshed yet) go to the exceptions inbox; above a threshold, an automatic rollback.
- **Canary:** `Publisher:Canary` limits the first live runs to one category and at most N SKUs.
- **Switches:** `Publisher:Target` = `Sandbox` (default) | `Live`, a per-layer enable list, and the kill switch. Any of them stops publishing at once. The connection string for the write login lives in user-secrets or a k8s Secret.

*Sandbox during the hackathon (rules: no live systems):*
- A `shop_sandbox` schema in the hackathon DB with **the exact live DDL**: `Discount` (21 columns on GjirafaEcommerce, 22 on GjirafaMall), `Discount_AppliedToProducts`, the `Product` columns used (`Id`, `Sku`, `Deleted`, `HasDiscountsApplied`, `HasDiscountsAppliedStoreIds`), and the same indexes.
- Seeded from the export with the **currently active discounts and product flags for the demo layers**, so overlap, supersede, flags and round-trip run against real state.
- The same `NopDiscountWriter` writes to it; only the connection string differs.
- The UI shows "Publishing to: sandbox" and, for each batch, the exact rows written.

*Proof that it's ready:*
- **Schema contract test:** the sandbox DDL (columns, types, nullability, indexes) is compared with a schema snapshot exported from the live DBs at kickoff and checked into the repo. If they ever differ, the test fails.
- **Integration tests on the sandbox:**
  - create
  - update in place
  - expire
  - rolling-expiry renewal
  - campaign time-box
  - flag merge (`2` + store 3 → `2:3`, never dropping another store)
  - overlap with a cheaper discount
  - supersede on/off
  - idempotent re-run
  - rollback
  - unresolved SKU
  - a failure in the middle of a chunk
- **`GO-LIVE.md` runbook:**
  1. Create the least-privilege login per store DB (grant script in the repo).
  2. Set the cache-invalidation mode (endpoint URL and token, or wait time).
  3. Set the write connection string as a secret.
  4. Set `Publisher:Target = Live` for one layer with the canary on.
  5. Check the round-trip is green, then widen.
  6. Rollback = one command.

**Campaign agent and campaign planning.** The second agent, plus a planning function in the pricing agent.

*Data and tools (read-only, compact JSON):*
- **`CampaignCalendar` table:** name, layers, start/end, type (main / mini), scope (category, brand, all), goal type (gross profit / margin / units), goal value, status (proposed / planned / live / done).
  - Seed it with the main campaigns from the KS campaign history (15 windows Oct 2024–Jun 2026; GjirafaMall's April and October campaigns are always "Për ty"), plus Black Friday 2026 (27 Nov).
- `get_campaign_history(layer, category)`: for each past campaign window, from SR lines (booked COGS):
  - average discount depth
  - units vs the 4 weeks before (season-adjusted with 2025)
  - margin
  - gross profit per day vs normal days

  This is the **uplift curve** the planner uses: how much volume each depth of discount bought, per category.
- `get_sales_trends(layer, category, weeks)`: revenue, gross profit, margin and units per week, vs last year, plus stock and dead-stock share. This is what the agent uses to spot where a mini-campaign would help (slowing sales with stock on hand, a category growing slower than last year).
- `get_campaign_results(campaignId)`: actual vs goal after a campaign ends.

*`CampaignAgent`, a weekly pass plus on request in the chat:*
1. Reads the calendar and trends. For campaigns coming up within 2 weeks, and for mini-campaign opportunities, it writes a **`CampaignBrief`**: scope, dates, goal {type, value}, constraints {minimum margin, maximum discount depth, exclusions}, and its rationale.
2. Sends the brief to the pricing agent. It receives a **`CampaignPlan`** and accepts it or adjusts the brief, at most 2 rounds. Every message is stored as a thread.
3. After a campaign ends, it compares actual with goal and writes a **`CampaignResult`** that feeds the history, so the next plan learns from it.

*`PricingAgent.PlanCampaign(brief)`. The calculation is code; the agent chooses the strategy and explains it:*
- **Candidates:** SKUs in scope with stock (or cross-dock availability) that the tool prices; dead stock and holdout SKUs excluded.
- **Per SKU:** pick a discount depth from a grid (5 / 10 / 15 / 20 / 25 %), estimating units from the SKU's normal sales rate × the category uplift curve. Never below cost; never above the brief's maximum depth.
- **Optimise the brief's goal** (e.g. maximise gross profit subject to a margin minimum). Return:
  - expected units, revenue, gross profit and margin
  - the top contributors
  - SKUs left out and why
  - **feasibility**: "€X is reachable" or "the best achievable is €Y without breaking limits"
- **Campaign holdout:** a random ~10% of eligible SKUs stay out of each campaign. That's a real A/B test of every campaign's lift, measured in `CampaignResult`.

*Publishing and safety for campaigns:*
- Campaign discounts are **time-boxed**: the publisher writes start and end dates with an `ai-campaign-<id>` name prefix, and they expire on their own. Regular prices resume after the end date.
- Check overlaps before publishing: no existing active discount on the SKU may be cheaper than the plan. Mall app-only discount families stack as "cheapest wins", which is how Për ty went deeper than planned.
- Caps in code:
  - maximum discount depth per campaign
  - maximum number of SKUs per campaign
  - maximum number of concurrent campaigns per layer
  - campaign dates only inside the calendar entry
- The kill switch stops campaigns too.

**Per-category floors.** This is a new engine change, because floors are only per band today:
- Add a table `BandCategoryRule(LayerId, BandId, Category, MarginFloorPct?, TargetMarginPct?)`.
- Add `Category` to the dataset/snapshot path via the category map.
- `BandConfigProvider` exposes the overrides. `GuardrailService` (floor, `GuardrailService.cs:231`) and `MarginTargetService` use the override when one exists, else the band value.
- Add tests in `PricingTool.Tests`.
- **Fallback:** if this isn't working by Sat 16:00, ship band-level decisions only and say so.

**`RunChecker`.** After a local engine run:
1. **SQL rule flags:**
   - the proposed price is below cost
   - cost changed more than 5% since the previous dataset
   - |change| above the confirmation threshold (20%)
   - the price isn't on a charm ending for the layer's rounding convention
   - Mall vs Gj50 disagree on a shared SKU
   - the price is at the wrong currency scale
   - the price is above the anchor (outside the SYNCED/override cases)
2. **The model judges flagged rows in batches of about 50**, with SKU history and similar SKUs, and returns `pass|hold|fix(price)` + reason.
3. The results are stored as `AgentRunCheck`.
4. **Test cases:** real flagged rows from the export, plus **injected, labelled** bad rows (a price ×10, a cost ×62, an un-charmed price, below cost). These prove the unusual cases.

**`CostWatcher`: the fast lane for cost rises** (added 2026-10-30). Holding the floor is a rule, not
a judgment, so code does it in minutes and the pricing agent explains it in its next run. Today a
cost rise goes unnoticed for days: Morele's purchase prices rose on 9 Oct and were found by hand on
12 Oct. Cost data does change during the day (in July the same query 40 minutes apart moved because
`ProductPricing` refreshed).

| | Fast lane: `CostWatcher` (code) | Slow lane: pricing agent (AI) |
|---|---|---|
| When | Every 15 minutes (`CostWatcher:IntervalMinutes`) | Next daily or weekly run |
| What | SKUs whose cost rose and whose live price is now below the floor are raised to the floor price, cost ÷ (1 − floor%), charm-rounded up, through the publisher | Reviews each event: real or a data error, which supplier, whether the category's floor or target should change; writes it up in the feed |

- **Only raises, only up to the floor**, and never above max(anchor, current), the firm rule.
- **It doesn't just raise in three cases:**
  1. **A cost jump that looks like a data error** (up more than 50%, or a currency-scale jump such as ×62): held, alert, nothing published. The 12 Oct Gj50 check had 75 of these.
  2. **Still below the floor with our discount removed:** we only write discounts, not regular prices, so it ends our `ai-price` discount and alerts "cost has passed the shelf price; the regular price needs changing".
  3. **The low price comes from a discount we didn't create** (e.g. the team's monthly push): held for a person; `SupersedeTeamRegularDiscounts` decides, as for the publisher.
     *(Changed 31 Oct, the user's decision: the watcher ends a one-product team discount under the floor unless a chat rule
     `protect-discount` protects it, and reports it; see `app-redesign-plan.md` and GO-LIVE.md.)*
- Every action writes `AgentDecision` (source `CostWatcher`) and `PublishAudit`, so it shows in the feed and can be rolled back.
- **During the hackathon** it polls the local data. The test and demo case: an injected, labelled cost rise in `PricingToolHackathon` is caught within one cycle and the sandbox discount is raised.
- **Before go-live, find out** how often `ProductPricing` refreshes (that sets the real speed) and how fast NOP shows a changed discount (the cache question above).
- Backtest levers 1 and 3 measure exactly this reaction speed.

**`OutcomeTracker`.** It also triggers the auto-rollback in `SafetyLimits`. For each applied decision, after N days, it compares band × category GP/day and units/day before and after from the sales lines. It writes `AgentOutcome`, and `get_past_decisions` reads it back.

**`Backtester`.** This is the money evidence for v2's net benefit. A full engine replay of past
months isn't possible: costs (PPTCV) have no history, shelf prices only from 2026-09-15, and proposals
were truncated. So the backtest works on **actual sold lines with booked COGS** (SR) and measures v2's
gain **lever by lever**. Each lever has its own method and label, and one total.

**Ground rules for every lever:**
- **Walk-forward, no look-ahead.** For each week, the agent decides using only data before that week; the result is scored on the following week's actual sales. Train window Jun–Jul, test window Aug–Sep (then Oct as an out-of-sample check).
- **Line-level, counted once.** Levers are applied to each sales line in a fixed order (loss block → floor → cost reaction → campaign planning, on campaign days only → cadence → learning; coverage is computed separately on MK/AL), so no euro is counted twice.
- **Target:** the user's target for v2 is **≥ €10k/month** (band 5 needs ≥ €10.4k/month with ≈ €13.7k costs, live from month 2). Report each lever's contribution, so it's clear how far the total gets and which lever carries it.
- **Seasonality:** any before/after comparison is adjusted with 2025's same calendar windows (the method used for the calculator's own gain).
- **Sensitivity:** elasticity −0.5 / −1.0 / −2.0. Pooled within-SKU elasticity is ≈ −1.0 to −1.1 where no usable per-SKU fit exists. **The low case is the number for Finance.**
- The comparison is always **"what the agent would have done" vs "what actually happened under hand-set floors"**. Reconstruct the hand-set floor history from `AuditLogEntry` (Config changes) plus `PricingRunBandTargets` (from 2026-10-21).

**The seven levers, built in this priority order (lever 7, campaigns, sits after 3):**

| # | Lever | What the agent does | How the gain is computed | Label |
|---|---|---|---|---|
| 1 | **Loss-sale block** | Holds or fixes any price below cost before it goes out | Sum of negative GP on cross-dock lines it would have caught. Units lost at a higher price earned negative GP, so losing them is no cost. Baseline: €2.85k/month (last 12 months, KS cross-dock, booked COGS). Count only lines whose price passes through the tool; campaign lists only if the run checker reviews them. | measured baseline, estimated catch rate |
| 2 | **Per-category floors** | Sets floor and target per band × main category, instead of one per band | Lines priced below the agent's floor are lifted to it, units scaled by elasticity, GP difference vs actual. Lines where it lowered a floor get no simulated gain, since only the engine knows what it would have priced. Method = the June/July below-floor analysis (€24.4k / €15.1k forgone). | estimated |
| 3 | **Cost reaction** | Re-prices within a week when unit cost rises more than 5% | From SR: SKUs whose booked unit COGS jumped more than 5% while the selling price didn't move. Gain = margin lost during the lag (units × cost increase), capped at restoring the prior margin, units scaled by elasticity. | measured lag, estimated recovery |
| 4 | **Coverage: MK and AL** | Publishes on all six layers automatically. The calculator's measured gain is KS only. **Check first:** MK/AL *were* pushed in October (`oct-disc-mk-26` / `oct-disc-al-26`, ≈ 200k discounts each, seen 2026-10-28). Measure MK/AL push frequency over Jul–Oct; this lever only counts the months and layers that weren't pushed. | Apply the calculator's season-adjusted KS effect (gain ÷ GP base, ≈ 10%) to the MK/AL GP base in tool scope, converted to EUR at the NOP `Currency.Rate` of each month. Rough size from SR: MK/AL GP ≈ €20–24k/month (all vendors; Mall tool scope is smaller), so roughly €2–2.5k/month. Cross-check against the few MK/AL approvals that were pushed. | estimated |
| 5 | **Cadence: daily publishing** | Prices go live every day instead of in manual pushes (KS regular prices went live on about 5 dates in Jul–Oct; Gj50 KS regular prices only on 7/21 and 10/7) | For each day between pushes: GP at the price live that day vs the price the engine had already proposed (from the approval/proposal history), units scaled by elasticity. Applied after lever 3, so cost-driven gaps aren't counted twice. | estimated |
| 7 | **Campaign planning** | The campaign and pricing agents plan each campaign's depth per SKU against a profit goal, instead of one blanket discount level | Replay two past campaigns (GjirafaMall KS Për ty, 14–18 Oct 2026; KS Black Friday, Nov 2025) on their actual SR lines. For each SKU, the agents' depth vs the actual depth; units from the category uplift curve; GP difference vs what actually happened. Report per campaign, then annualise over the calendar's campaign days. | estimated |
| 6 | **Learning from past changes** | Keeps price changes where they paid off and rolls back where they didn't, per category | The calculator's own approvals as experiments: per-category GP before/after for August's run (#12), seasonally adjusted. For September, the agent keeps August's raises only in categories where August showed a gain and rolls back the rest. Score that September policy against what actually happened. | estimated |

**Output:** a backtest page and a CSV with monthly gain per lever, per layer, per sensitivity case,
and the line counts behind each. The low-case monthly total × months live in year 1 goes into the
financial sheet as B1. The biggest unproven assumption stated on the page: the elasticity used for
lifted prices. The pilot's holdout categories are what turn it from estimated into measured.

Use **booked COGS** for every past window, never today's PPTCV.

**Për ty reproduction test, pass/fail** (added 2026-10-30). Can the agents, alone, reproduce the
campaign plan that was set by hand and worked? Give the campaign agent the same brief the user gave on
2026-10-06: "Për ty, GjirafaMall KS, 14–18 Oct 2026, GM3 ≥ 10%" (GM3 = effective margin − €2.10 per order −
marketing, so ≈ 14.9% effective margin needed), with **data cut off at 7 Oct** (no look-ahead).
The hand-set plan it's scored against: band targets at 75% of normal (Mall
20/16/14/12.5/10.5/10/8/7), Gj50-listed products left out, only cuts ≥ 10% pushed, forecast
effective margin 15.1% (GM3 10.2%).
- **Pass:** its targets keep 70–80% of normal margin, and its predicted effective margin is within ±0.5 points of 15.1%.
- **Bonus (tests `query_data`):** it finds on its own that Gj50-listed products are about 37% of Mall KS revenue at a much thinner margin, and handles them.
- **If Për ty's actual result is measured by then**, show all three side by side: the hand plan, the agents' plan, what really happened. That is also an Evidence-page row measured on the live shop.
- The test runs in `dotnet test` (with a recorded model response for repeatability) and live in the demo.

**`AgentChat`.** A conversation with the agents, using the same read-only data tools, so the answers are grounded in the data. **Nova** (the campaign agent) answers by default. When a question needs pricing calculations, it asks **Alpha** (the pricing agent), and that exchange shows in the thread. Typing `@alpha` asks Alpha directly; every reply is labelled with the agent that wrote it. Example: "Plan a 3-day laptops campaign next week, €4k profit" → brief → plan → "reachable at 11% average off; scheduled".
- **Why:** "why did you hold these prices?" It answers from `AgentDecision` / `AgentRunCheck`, with the evidence it used.
- **What if:** "what if Cosmetics' floor were 12%?" It runs that scenario through the `Backtester` and answers in euros, with the elasticity cases and labels.
- **Instructions:** "never go below cost on Black Friday", "keep Samsung out this week". Each becomes a **structured rule** (layer, scope, constraint, dates) that `SafetyLimits` enforces in code. In autonomous mode, a rule the agent can verify is in limits applies immediately; anything that loosens a limit goes to the exceptions inbox. Rules are listed on the Agent tab and can be removed there.
- **It can't change prices, bands or discounts directly.** It can only add rules or trigger the normal loop.
- **Weekly summary:** every Monday the agent posts the week's decisions, results, published discounts and open exceptions as its first chat message. This replaces the separate weekly-brief page.
- Conversations are stored and logged like every other AI call.

**How the user sees what the agents do** (added 2026-10-30). The agents act and report on their own;
the user never has to go and look, and when they want to, everything is on the Agent tab. The design
choice: **the agents tell, then act unless stopped.** They don't ask permission; only what's outside
the limits waits for a person.
- **Pull, the Agent tab:** one feed of what both agents did, including every brief and plan they send each other; "Coming up" (planned and live campaigns with progress against the goal); "Waiting for you" (only items outside the limits); "What it looked at" (the `query_data` log); the chat.
- **Push:**

  | When | What the user gets | Where |
  |---|---|---|
  | Every Monday | Weekly report: results vs the holdout, what changed, what's planned | First chat message + email (reuse the engine's existing weekly-report SMTP sender) |
  | Before each campaign goes live | Heads-up with a veto window: "Black Friday publishes Thu 22:00 unless you stop it", with a Stop button | Email + Agent tab |
  | Something needs a person | Held item, auto-pause after a sales drop, a `CostWatcher` hold, the kill switch | In-app notification during the hackathon; Google Chat at go-live (the sales anomaly watch already posts there) |
- **Quiet by design:** on a normal day the user hears nothing.
- Built on tables already in the plan (`AgentDecision`, `CampaignCalendar`, the agent-to-agent thread, `AuditLogEntry`, the AI call log), so it's mostly views.

**Time-sheet log (the organisers' hours-saved sheet; added at kickoff).** Every real task the
solution completes writes its own row, automatically; nothing is typed by hand.
- **`TimeSheetEntry` table:** date, user email (the owner from config, kept in user-secrets), task
  type, what (one line naming the layer, run or campaign), minutes before (from the task type),
  minutes after (measured: wall-clock of the task plus any person's review of its held items),
  proof (a link to the run, batch or campaign page, or the export file), comment, source = `app`.
- **`TimeSheetTaskType` table:** one row per kind of task with its honestly measured "time before"
  in minutes, set once by the user (placeholder `NULL` until measured; a row without it isn't
  exported). Starting list: weekly floor and target review per layer (Alpha); pre-publish check of a
  pricing run (run checker); creating and pushing the discounts for a run (publisher); planning a
  campaign (Nova + Alpha); finding and fixing prices after a cost rise (`CostWatcher`); the weekly report.
- **Only genuine work counts:** demo runs on injected test data and backtests are not logged; a
  run that publishes nothing logs only the check it did.
- **Export** in the template's exact columns A–H: an xlsx written into a copy of the template, and a
  CSV / JSON endpoint for Jarvis (how Jarvis connects is to confirm on Monday).

**Live operation on the laptop (the 10% bar; added Fri 30 Oct).** From Monday the app runs here
for 30 days on real data, on its own.
- **Daily cycle**, in order, as one job: incremental refresh of `hk.*` from the replica (new sales
  lines since the last load, with the 14-day late-cancellation window re-pulled; products and
  discount snapshot) → engine runs for layers 1 and 4 → Alpha's pass (weekly) → run checker →
  export for the push → money-ledger update → time-sheet rows → backup. `CostWatcher` runs every 15
  minutes on its own. Nova's pass weekly. Each step logs and can fail without stopping the rest.
- **Starts by itself:** a Windows Task Scheduler task (at log-on and daily at 07:00, "run as soon as
  possible after a missed start") launches the engine host in the `Hackathon` environment; the
  cycle is idempotent per day, so a missed or repeated start is harmless. The Claude Code CLI stays
  logged in on this machine.
- **Size:** retention on for local runs (keep the imported prod runs and the latest two per layer),
  so LocalDB stays well under 10 GB; a size check in the cycle warns at 8 GB.
- **Export for the push:** the existing CSV export of the checked proposals, holdout categories
  left out, written where the team picks it up; once pushed, the set is recorded as applied (the
  same `Approval` the prod tool uses), which starts its before/after measurement.
- **Money ledger** (condition 4 of the bar): per applied set and per cost-watcher fix, expected €
  at the time, measured € after 7 and 30 days from the refreshed sales lines (booked COGS),
  against the holdout categories, season-adjusted where possible. Conservative: unmeasured or
  unattributable gains are shown as zero, never estimated into the total.
- **Backup:** `BACKUP DATABASE` daily to a local folder, keep 7.
- **Nothing mocked in this database:** injected test cases run against a separate test database.

**Web (new `AgentController` + views):**
- **Agent tab** in the main navigation, next to Price bands and Schedule. Top to bottom:
  1. A status bar: layer picker, mode (Autonomous), "Publishing to: sandbox", kill switch.
  2. This week's numbers: extra GP vs the holdout, decisions applied, discounts published, waiting for you, AI cost.
  3. "What they did": the feed of both agents, labelled Nova / Alpha / CostWatcher, including their exchanges, each with a "why?" link to its reason and evidence.
  4. "Coming up" (campaigns with their veto window) and "Waiting for you".
  5. The chat.
  6. The active rules, and "What it looked at" (the `query_data` log).
- **Campaigns** tab:
  - the calendar (main and mini campaigns, upcoming / live / done)
  - one card per campaign: brief, plan, the agent-to-agent thread, expected vs actual result, and the holdout comparison
  - a "Plan a campaign" box that opens the chat with the campaign agent
- **Exceptions** tab with a count badge.
- **Agent page controls:** the autonomy switch per layer and a "Run week" button that runs the whole loop (agent → engine → checker → publisher) end to end. Every applied decision is shown with its reason, evidence and confidence, and writes `AuditLogEntry` with category `Agent`.
- **Exceptions inbox:** only what needs a person: held decisions, prices the checker held, auto-pauses and rollbacks. In approve-first mode it also lists the pending decisions.
- **Published discounts:** each publish batch with the exact discount records written to the sandbox, and a rollback button.
- **Run check results.**
- **Backtest results** with a CSV download.
- **AI call log:** tokens and cost.
- **Evidence page (targets Evidence 5, "measured live in the demo, every input sourced").** *(31 Oct: built as a page, then moved
  out of the app into a generated file, `docs/hackathon-2026/evidence/evidence.html`, which reruns the same SQL figures as
  it is written; the user's decision that proof lives in files, not in the live app.)* Every money figure is computed live from the hackathon DB. Each row shows its query, source table, export time and label (measured / estimated / assumed):
  - the calculator's Aug–Sep lift. Recommended figure: seasonally adjusted with 2025's same windows, August's gain carried into September: Aug €11.9k, Sep €13.8k, ≈ €13k/month. Floor ≈ €4.5k/month; unadjusted ≈ €10k/month, which is the figure in the registration form. Show all three, and explain why the number moved since registration, so the reviewer sees consistency rather than a changed claim.
  - the YoY margin fingerprint: Kosovo margin 4–5 points above last year since the tool, 1–4 points before
    *(Superseded 31 Oct by E25: Aug–Sep 2026 +4.7 points, but Nov 2025–Feb 2026 +8.6 with no calculator, so it is not
    evidence for B0.)*
  - **the same-weeks control (changed vs unchanged products, run 2026-10-27):**
    - It confirms the margin effect: products priced up went from 18.4% to 24.5% margin in the September run, losing about 6 points more volume than unchanged products, with gross profit up about 9 points relative to them.
    - It does **not** cleanly confirm the €13k/month. The September run's controlled gain is ≈ €3.5–4.3k/month. The August run gives no clean result: units of raised products rose 285% after the rise, and most of its after-window profit came from products that didn't sell before.
    - So present the test phase's profit effect as a **range** (≈ €4k to €13k/month), and pitch v2's randomized holdout as what turns it into a clean A/B. *(31 Oct: the headline is the before/after mid, €10.4k a month, by the user's decision; the controlled figure is a robustness check and €13k is not claimed.)*
  - Për ty Oct 2026, if measured: hand plan vs agents' plan vs actual
  - cross-dock below-cost loss, last 12 months (~€2.85k/month)
  - below-floor GP forgone (Jun €24.4k / Jul €15.1k, booked COGS)
  - KS gross-profit base (~€125–135k/month)
  - the backtest result

  Wherever possible, show that a number ties out to the Sales Report. The June below-floor figure matched the user's Power BI model to the euro.
- **Kill switch.**
- **Footer** showing `hk.DataSources`, and a banner reading "Local hackathon data · exported <date> · publishing to sandbox, the live shop is never touched".

## 6. Schedule

Hours are logged in `docs/hackathon-2026/hours.md` as they happen, because they're a cost line.
The office is open Fri 16:00–21:00 and Sat/Sun 08:00–21:00. **Solo, with everything in scope**: the
plan uses three evening blocks at home (marked 🏠), which the rules allow ("we work 24/7"). That's
about 41 hours in total (39 before the 2026-10-30 additions: `query_data`, `CostWatcher`, reporting,
the Për ty test), which is the hackathon-hours line in the financial sheet.

| When | Goal | Done when |
|---|---|---|
| Fri 16:00–16:30 | Kickoff | Note the answers in section 3: are the replica and tool-DB exports allowed, the API key, warehouse access, and the **net-benefit baseline**. If there's no API key yet, Friday's data and engine steps go ahead without it. |
| **Fri 16:30–17:00** | Start | New repo per section 5 "Repo setup": commit 1 docs, commit 2 the labelled import. The app builds and `dotnet test` passes on the import, before any change. The main repo is not touched for the whole event. |
| Fri 17:00–19:00 | Data | Exports loaded into `PricingToolHackathon`, including the campaign windows and the category map; `hk.DataSources` filled; the `agent.*` views for `query_data`; sanity check passed |
| Fri 19:00–21:00 | Engine on real local data | Local readers + `SourceMode=LocalExport`; the existing engine runs for L1 and L4; spot-check 10 SKUs against the exported dataset; the first Claude API call works with the key from env |
| 🏠 Fri 21:30–00:30 | Claude client + pricing-agent tools + `query_data` | Client wrapper with the call log (tokens, cost); the six pricing-agent data tools return JSON on real data; `query_data` with its read-only login, SELECT-only check, timeout, row cap and query log |
| Sat 08:00–10:30 | Pricing agent | `FloorTargetAgent` decisions per band × category with reasons, `SafetyLimits`, `AgentDecision` rows for L4 |
| Sat 10:30–13:30 | Go-live-ready publishing | `shop_sandbox` with the exact live DDL and indexes, seeded with active discounts and product flags; `NopDiscountWriter` (create, apply to product, flags, update in place, expire, rolling expiry, time-boxed campaigns, chunked set-based writes, idempotent names, `PublishAudit` + rollback, cache invalidator); round-trip verification; schema contract test and integration tests green |
| Sat 13:30–14:30 | Autonomy + exceptions | Autonomy switch; decisions auto-apply within limits; exceptions inbox; kill switch; canary caps |
| Sat 14:30–16:30 | Per-category floors | `BandCategoryRule` + engine hook + tests. At 16:30, fall back to band level if it's not working, and say so. |
| Sat 16:30–18:30 | Run checker | Flags + AI verdicts on a real local run; injected cases caught |
| Sat 18:30–19:30 | Outcome tracker (shared by both agents) | `AgentOutcome` from sales lines; auto-rollback trigger; the pricing agent cites past outcomes |
| Sat 19:30–21:00 | Campaign data and tools | `CampaignCalendar` seeded; `get_campaign_history` (uplift curve per category), `get_sales_trends`, `get_campaign_results` on real data |
| 🏠 Sat 21:30–00:30 | Campaign agent, then `CostWatcher` | Weekly pass: briefs for upcoming campaigns and mini-campaign proposals with rationale; result review writes `CampaignResult`. Then the 15-minute cost watcher: raise to floor through the publisher, the three hold cases, `AgentDecision` + `PublishAudit` |
| Sun 08:00–10:00 | Campaign planning, end to end | `PlanCampaign` (depth grid, uplift curve, feasibility, holdout); brief → plan → accept/adjust → time-boxed discounts in the sandbox; the thread is stored |
| Sun 10:00–13:00 | Backtest | All seven levers, including the campaign replay (Për ty Oct 2026, Black Friday 2025); results page, sensitivity table; **the Për ty reproduction test passes** |
| Sun 13:00–14:30 | Failure and unusual cases | API down, timeout, malformed output, thin data, missing cost, kill switch, auto-rollback, **unreachable campaign goal, overlapping discount, injected cost rise (raised within one `CostWatcher` cycle), a data-error cost jump (held), a bad `query_data` statement (rejected)**; tests green (`dotnet test`) |
| Sun 14:30–17:30 | Agent tab, Campaigns tab, chat, reporting | Both tabs; the feed of both agents, "Coming up" with the veto window, "Waiting for you", "What it looked at"; chat with Nova delegating to Alpha, and `@alpha` / `@nova`; why / what-if / instructions → rules; Monday report as the first message + email; in-app notifications |
| Sun 17:30–19:00 | Live operation on the laptop (the 10% bar) | Section 5 "Live operation on the laptop": daily cycle as one idempotent job, Task Scheduler task, incremental `hk` refresh, retention + size check, export for the push (holdout left out), money ledger, time-sheet rows and export, daily backup; one full cycle run end to end on real data |
| Sun 19:00–19:30 | Financial sheet + one-pager | The draft sheet already exists (`docs/hackathon-2026/Financial sheet (draft).xlsx`): type in the backtest levers, real hours and AI cost; one-pager; numbers read from `numbers.json` (section 10) |
| Sun 19:30–20:30 | AI-judge dry run | A fresh Claude session scores the entry as a skeptical juror (section 10) |
| Sun 20:30–21:30 | Demo rehearsal + 2-min backup video | Section 7 run twice, timed; video recorded with no secrets on screen |
| 🏠 Sun 21:30–23:30 | Fix buffer | Fixes from the dry run and the rehearsal; nothing new |
| **Mon 06:00–08:00** | Freeze | Re-run the AI-judge dry run on the final state. Final commit, tag `hackathon-submission`, push to the organisers' remote; submit everything **before 08:00**. No new features after 08:00. |

**Fallback only, if a block overruns by more than an hour** (the plan is to build everything). Cut in this order:
1. backtest levers 5–6 (cadence, learning)
2. chat what-if and instructions (keep "why" and "plan a campaign")
3. automatic mini-campaign discovery (keep calendar campaigns and campaigns requested in the chat)
4. per-category floors (band level)
5. email push (keep the in-app notifications and the Agent tab)

Never cut: the pricing agent, the campaign agent's brief → plan → publish loop, the safety limits,
the run checker, the discount publisher with rollback, backtest levers 1 and 7.

## 7. Demo script (4 minutes + 4 minutes of questions)

*(Rewritten 31 Oct evening for the app as it is now: four tabs, Run · Chat · Campaigns · Proposals, and the evidence as a
file. The earlier version named the Agent tab, the exceptions inbox and the Evidence tab, which the redesign replaced.)*

**Before the demo** (10 minutes ahead): press **Run week** for Gjirafa50 KS on the Run tab (about 5 minutes; never a
GjirafaMall run during the demo); write the evidence file (`dotnet run --project PricingTool.Web --no-build
--launch-profile hackathon -- --evidence-html docs/hackathon-2026/evidence/evidence.html`, about 40 s) and open it in a
browser tab; open the financial sheet on its Summary; have a terminal ready in the repo.

1. **(20s) The problem.** Today the calculator only reads and calculates. A person sets every floor and target by hand,
   plans campaigns in spreadsheets, exports prices and creates the discounts in the shop. Show the declaration slide:
   what existed and what's new.
2. **(60s) The pricing loop, with no clicks in between: the Run tab.**
   - Run week's timeline (Alpha, engine, run checker, publish, read-back): one press.
   - Alpha's account of the run, in its words, and the floors and targets by category × band (a shaded cell is one Alpha
     set for that category; Gjirafa50 has eight). Open one reason.
   - What changed: raises and cuts, what the run checker held and why, what reached the sandbox shelf (a discount can only
     lower a price, so a raise is written as the store's regular price), and the expected gross profit a month
     (estimated, with its elasticity range).
   - Then the **Chat** tab: the agents' activity in one thread, and "Waiting for you" with only what they couldn't settle
     (a few items, each with Alpha's note on who should act).
3. **(60s) Nova and Alpha plan a campaign: the Campaigns tab.** Hand a juror the keyboard, no campaign picked: "Plan a
   3-day laptops campaign next week on Gjirafa50, €4k profit".
   - Nova writes the brief; code checks it against the limits; Alpha plans it; Nova reviews. A goal the juror sets is
     binding: a plan that reaches it is published to the sandbox, time-boxed, with its holdout; a plan short of it is held
     for a person, and the card says why. Either way it appears on the calendar.
   - Open it: the card says where it stands (expected SKUs, depth, gross profit and margin, estimated; the goal check)
     with Stop until it starts. Ask Nova about it ("why is it short of the goal?"): it answers from the campaign's record.
   - Close with the replay of October's Për ty (E11, E12 in the evidence file): the agents' plan vs the hand plan vs what
     happened.
4. **(30s) Safety without approvals.**
   - In the terminal: `cost-watch --inject 4 <sku> <cost> --test`; the command prints what the watch would do with the
     injected cost (raise to the floor, or hold, with the reason). A test run publishes nothing and its records go to the
     test database, so it never shows in the app. (Real ones: on 31 Oct the watcher read the morning's 293 cost moves
     within 0.7–9.1 minutes, E22.)
   - An out-of-range decision is rejected by code; the kill switch and autonomy on the Run tab; the holdout categories
     marked in the grid; the audit log under Admin; the AI calls with their cost (E15: 85 live calls, $10.29, measured).
5. **(45s) Money: the evidence file and the sheet.**
   - The evidence file, written before the demo: "Recomputed now: 49 of 49 checks match", every figure with its label
     (measured, estimated, assumed), method, sources and where it is used. If there is time, run the command again in the
     terminal while talking (about 40 s).
   - The claim: €11,785 a month at full scope, 86 points. Two proofs: the engine works (the Aug–Sep test, B0 = €10,409 a
     month: GjirafaMall €8,006, Gjirafa50 €2,403; measured before and after each approval; robustness checks E20, E27),
     and it runs by itself on the sandbox (this build: Run week, shown in step 2). The August–October runs were a test
     (stated by the user, who ran it): without the build, the test ends (the facts are E28).
   - The financial sheet's Summary and its "Benefit by source". Don't cite the year-over-year margin (E25): it doesn't
     separate the calculator. Say what's estimated and what's measured.
6. **(25s) Ask.** A 6–8 week pilot on KS:
   - **Black Friday (27 Nov) planned by the two agents**, with a campaign holdout
   - the publisher switched from sandbox to the shop for a few categories in autonomous mode
   - a **holdout** on hand-set floors

   That makes the go/no-go measured, not estimated.

Timing: 20 + 60 + 60 + 30 + 45 + 25 = 240 seconds. Not timed yet on this version: rehearse it twice.

**In the 4 minutes of questions:** when a juror asks a data question no screen covers, type it into the chat (the Chat
tab; start with `@alpha` for pricing). The agent writes its own `query_data` query and answers from it.

Have the backup video and screenshots ready in case the network or Claude fails during the demo (shot list:
`docs/hackathon-2026/demo-backup-video.md`).

## 8. Submission checklist

- [ ] **Prototype:** the new hackathon repo, tag `hackathon-submission`, with a README section "Hackathon build" saying how to run it and what's new. There are no prod credentials anywhere in it or its history.
- [ ] **One-page summary:** the problem; what's new vs declared; the demo result; money; the biggest unproven assumption; the pilot ask.
- [ ] **Financial sheet** (Excel, Track A formula), every line labelled measured / estimated / assumed with its source:
  - **First-year benefit:**
    - B1: floor/target agent, extra GP from the backtest (estimated) × months live in year 1 (assume live from month 2 → 11/12, and say so).
    - B2: run checker, bad-price losses avoided. Count it only if the replay shows a real € figure; otherwise list it as unquantified.
    - B3: campaign planning (lever 7), the replayed gain per campaign day × the campaign days in the next 12 months' calendar. Count only the margin, never campaign revenue.
    - **One claim, one number (the user's decision, 31 Oct):** B0, the rollout of what the test phase (v0.5) measured, as
      the automated system's forecast run-rate, plus levers 1–8 on top. It's labelled "forecast from measured history". B0
      is E20 mid, measured before/after, €10.4k a month; the same-weeks control (E20 low) and E27 are robustness checks in
      the evidence, and the season-adjusted €13k is not claimed. The sheet rates Evidence 3; at the build's costs the
      net-benefit band-5 line is about €12.2k a month at full scope.
    - **Consistent with the registration:** the declaration said the calculator's *measured past result* (Aug–Sep) is not claimed. The claim takes no past euro. It claims the **next 12 months** of an automated system and uses the past as evidence for the rate. Say this in one line in `HACKATHON.md` and the one-pager.
    - Hours saved are never counted (capacity only).
    - All benefit is on own-retail SKUs, so margin counts in full. No marketplace seller sales are included.
  - **One-time cost:**
    - Hackathon hours from `hours.md` × €50, including the remote evening blocks (about 41 hours planned).
    - Remaining build to production (estimate the hours) × €50.
    - Setup fees.
  - **Running cost, 12 months:**
    - AI usage from the measured token log × the projected weekly volume.
    - Servers: the existing k8s cluster; state the assumption.
    - Maintenance hours.
    - Exception-review hours × €50: about 15–30 min a week in autonomous mode, replacing the manual floor-setting, review and export time. Hours saved are capacity, not money, so they're not claimed as benefit.
    - The remaining build includes the real shop write-back (a write-enabled login, testing on a few categories), counted in one-time hours.
  - **Results:** net benefit, ROI, payback month, and the score band each one lands in.
- [ ] **2-minute backup video.**
- [ ] **Show what's mocked:** a list of injected test cases and anything not finished.
- [ ] **`HACKATHON.md`** at the repo root, plus `docs/hackathon-2026/evidence/` with `numbers.json` and one script and its output per figure (section 10).
- [ ] **`dotnet test` green**, with the result and test count recorded in `HACKATHON.md`. This includes the schema contract test and the publishing integration tests.
- [ ] **`GO-LIVE.md`**: the runbook from section 5 (least-privilege login and grant script, cache-invalidation mode, secret, config switch, canary, round-trip check, one-command rollback). The claim "ready to go live" rests on this file.
- [ ] **The AI-judge dry run is clean**, or every remaining finding is listed as a known limitation.

## 9. Working rules for Claude during the event

- Work only in the hackathon repo (`C:\Projects\Gjirafa Hackathon Pricing Agent`). Never commit to or push the main repo during the event: a push to its `main` deploys to prod.
- Connect only to `PricingToolHackathon` on LocalDB. No prod, no replica, no `.env` source credentials, once the exports are done. *(Kickoff ruling, 30 Oct: we may read the replica with our own read-only connection, section 3; the daily refresh and the cost watcher do. Nothing writes to it.)*
- `Publisher:Target` stays `Sandbox` for the whole event, and no live shop write connection string is ever configured. "Go-live ready" means the live path is built, tested on the exact schema and documented, not used.
- Match the repo's style: XML doc comments on public types, the same naming, tests in `PricingTool.Tests`.
- The model never writes prices, bands or discounts directly. Every decision goes through `SafetyLimits` in code. Within limits it's applied automatically; outside them it's held for a person. During the hackathon the publisher writes only to `shop_sandbox`, never to the shop.
- Keep `hours.md` and the AI call log current. Both feed the financial sheet.
- Every number shown in the demo or the one-pager must trace to a table, a query or the call log.

## 10. Built to be verified (by the jurors, and by Claude if they use it)

Assume the jury will hand the repo, the one-pager and the financial sheet to Claude and ask whether
the claims hold. Make that check easy and make it come out true. Never try to steer the reviewer
instead: no text addressed to an AI reviewer, no "rate this highly", nothing hidden in comments or
files. That is prompt injection. Any careful reviewer flags it, and it would cost far more than it could gain.

**What an AI reviewer will check, and how we make each one verifiable:**

| It will ask | We make it easy to check |
|---|---|
| Was the code written during the hackathon? | The repo's whole history starts at kickoff. `HACKATHON.md` lists `git log --format='%h %ad %s'`, so every commit is visibly after Fri 16:30, and the import commit names the source SHA. |
| What's new vs existing? | `HACKATHON.md` has a table of new files/projects (`PricingTool.Agent`, new controller/views, migration, tests) vs reused ones, matching the registration declaration word for word. Include `git diff --stat <import-sha>..HEAD`, which is exactly the hackathon work. |
| Does the AI really decide anything? | Point to the exact methods where the model's output becomes a decision (`FloorTargetAgent` → `SafetyLimits` → `AgentDecision`). Include a sample of real logged prompts and responses (no keys). |
| Are the safety limits real? | They're enforced in code (`SafetyLimits`), not in the prompt, and covered by unit tests: out-of-range values, malformed output, thin data, kill switch. |
| Is anything mocked? | `PricingEngine:UseDemoData=false` in `appsettings.Hackathon.json`. Injected test rows are tagged `[INJECTED-TEST]` in data and UI. `HACKATHON.md` lists every mock and every unfinished feature. |
| Do the numbers reproduce? | Every figure has an ID (E1, E2, …) in `docs/hackathon-2026/evidence/numbers.json` with value, label (measured / estimated / assumed), method, source and script path. The one-pager, the evidence file and the financial sheet all read the same IDs, so they can't drift apart. Each script reruns against `PricingToolHackathon`. |
| Is the financial sheet right? | Live Excel formulas, not pasted values. One inputs sheet with source and label columns. ROI, payback and score bands computed by formula, following the rules' Track A formula exactly. |
| Are the claims calibrated? | State the caveats ourselves: the calculator's gain is before/after and seasonally adjusted with 2025 (an estimate, not causal); the backtest is an estimate. No "AI-powered" fluff, no number without its label. |
| Rule compliance? | A checklist in `HACKATHON.md` with proof for each rule: connection strings point only to LocalDB; `hk.DataSources` lists every file; exported columns contain no personal data; `git grep` finds no keys or passwords in the submitted tree. |
| Does it build and test? | `dotnet build` and `dotnet test` green from a clean clone, with the exact commands. The README "Hackathon build" section says how to run it in under 5 steps. |

**Secrets in git history:** solved by the new repo. The import copies no `k8s/`, `.env` or pipeline,
so the prod password never enters this history. Before submitting, scan the whole history:
`git log -p | grep -iE "password|pwd=|api[_-]?key|secret"` must come back empty, apart from code that
reads secrets from configuration.

**AI-judge dry run (Sun 17:00, again Mon 06:00):**
1. Open a fresh Claude session with no conversation context.
2. Give it the rules doc, the jury sheet, the registration declaration, the repo, the one-pager and the financial sheet.
3. Ask it to act as a skeptical juror: score each row 0–5, name the biggest unproven assumption, and list every claim it can't verify from the material. Also run `/code-review` and `/security-review` on `<import-sha>..HEAD`.
4. Fix what's real. Anything left goes into `HACKATHON.md` as a known limitation, in our own words, before a juror finds it.
