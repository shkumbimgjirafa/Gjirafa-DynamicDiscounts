# Prompt 01: Fri 17:00–21:00, config, Agent project, engine on live data, Claude client

Paste into a new Claude Code session opened in `C:\Projects\Gjirafa Hackathon Pricing Agent`.

---

We're building Pricing Tool v2 for the Gjirafa AI Hackathon. Read `CLAUDE.md` first, then
`docs/hackathon-2026/kickoff-plan.md` sections 1, 3 (the kickoff answers), 4 and 5 up to "Agent data
tools". This block covers the schedule rows "Data" (the config part) and "Engine on real local data"
in section 6.

**Already done (don't redo):**
- The repo has three commits: docs, the labelled import of the calculator, and the kickoff answer.
  `dotnet test` passes (692) on the import.
- LocalDB database `PricingToolHackathon` exists with the existing EF migrations applied.
- User-secrets id `gjirafa-hackathon-pricing-agent-2026` already holds
  `ConnectionStrings:SourceReadOnly` (the NOP replica, a read-only login) and `SourceDataset:Mode`.
  Never print or copy these values.
- **Another Claude session (the data session) is loading data into `PricingToolHackathon` right now:**
  the tool's own state for the KS layers (layers, bands, settings, elasticity, dead-stock state,
  approvals, band-target history) into the `PricingTool.*` tables, and analysis tables into an `hk`
  schema (sales lines with booked COGS and a cross-dock flag, campaign windows, category map, cost
  history, discount schema and active KS discounts). Each load writes a row to `hk.DataSources`.
  **Don't drop or recreate the database, and don't write to `PricingTool.*` or `hk.*` data.** Your
  new tables go in new EF migrations.

**Do, in this order, committing after each step with `dotnet test` green:**

1. **Hackathon config.**
   - Add `<UserSecretsId>gjirafa-hackathon-pricing-agent-2026</UserSecretsId>` to
     `PricingTool.Web.csproj` and `PricingTool.Engine.csproj` (replace the Engine's existing id).
   - Load user secrets when the environment is `Hackathon` (by default ASP.NET only loads them in
     Development), in both Web and Engine.
   - Add `appsettings.Hackathon.json` to Web and Engine: `PricingToolDb` → `(localdb)\mssqllocaldb`,
     database `PricingToolHackathon`; `PricingEngine:UseDemoData = false`. No secrets in it.
   - Check that the app starts with `ASPNETCORE_ENVIRONMENT=Hackathon` and reads the replica
     connection from user-secrets.
2. **`PricingTool.Agent` project:** class library, net8.0, references Core and Data; add it to the
   solution and reference it from Web, Engine and Tests. Retitle `README.md` so it no longer says
   "v1" (the declaration says the calculator has no AI model; see plan section 2).
3. **Engine on live data.** Wait until `hk.DataSources` has a row for `PricingTool.Layers` (the data
   session's signal that the KS config is loaded). Then run the existing engine, unchanged, for
   layer 1 (GjirafaMall KS) and layer 4 (Gjirafa50 KS) against the replica. Spot-check 10 SKUs per
   layer: the engine's dataset row (price, old price, PPTCV, stock) against a direct read-only query
   on the replica. Record the run ids, duration and spot-check result in `hours.md`.
   Replica queries must stay light (CLAUDE.md).
4. **Claude client wrapper** in `PricingTool.Agent`. **Load the `claude-api` skill first.** Use the
   official C# SDK (NuGet `Anthropic`).
   - Options `Agent:*`: models per role (Alpha and Nova `claude-opus-5-5`, run checker
     `claude-sonnet-5-5`), max tokens, timeout, `Agent:Enabled` (the kill switch).
   - Tool use with JSON-schema tools; structured output validated against a schema; prompt caching
     on the system prompt and data dictionary; retries and timeouts.
   - **`AiCallLog` table** (new migration): time, agent (Alpha / Nova / RunChecker / Chat), model,
     input, output and cached tokens, latency, cost in USD, success or error. This is the measured
     AI-cost line of the financial sheet.
   - **The API key isn't here yet.** It will come from user-secrets (`Anthropic:ApiKey`) or the
     `ANTHROPIC_API_KEY` env var. Until then, write the wrapper and unit tests against a fake
     transport, and make the app start and show "AI: no key configured" instead of failing.

**Done when:** the app and engine run in the `Hackathon` environment on the replica, the engine has
produced runs for layers 1 and 4 with a recorded spot-check, `PricingTool.Agent` exists with a tested
client wrapper and `AiCallLog`, `dotnet test` is green, and `hours.md` has this block's line.
If something can't be done, stop and say what and why rather than working around a rule in `CLAUDE.md`.
