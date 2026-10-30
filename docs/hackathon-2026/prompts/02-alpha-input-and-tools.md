# Prompt 02: Alpha's input, data tools and query_data

Detail for the block after prompt 01. The build session reads it itself when it gets there
(`CLAUDE.md`: keep going, never wait); nobody needs to paste it.

---

Block: plan section 6, the row "Claude client + pricing-agent tools + `query_data`". Re-read `CLAUDE.md` and plan section 5 from
"Agent data tools" to "`SafetyLimits`", plus "`query_data(sql)`". Pull first: the data session has
added `docs/hackathon-2026/agent-data-dictionary.md` and the `agent.*` views
(`docs/hackathon-2026/exports/agent_views.sql`, already applied to `PricingToolHackathon`).

**What's in the DB for you (read-only for your code; the data session owns these):**
`agent.Layers`, `agent.Products`, `agent.SalesLines` (with `BandId`, category, `StockClass`),
`agent.DailySales`, `agent.CampaignHistory`, `agent.CostChanges`, `agent.BandConfig`,
`agent.BandTargetHistory`, `agent.TestPhaseBatches`, `agent.TestPhasePriceChanges`, plus the
`PricingTool.*` tables (incl. `SkuElasticity`, dead-stock state) and your own engine runs.

**Build, committing after each step with `dotnet test` green:**

1. **`AlphaInputBuilder`** (C#, no AI): for one layer, a scorecard with one row per band × main
   category (Mall: `MainGroup`; Gj50: `CategoryL1`) over the last 30 days, ending 14 days ago
   because of late cancellations, plus the same weeks of 2025:
   units, net revenue, gross profit, margin; share of revenue sold below the band's floor and at
   the floor; cross-dock share; cost moves (`agent.CostChanges`); stock value and dead-stock share
   (latest engine snapshot / dead-stock state); elasticity coverage (`SkuElasticity`); today's floor
   and target; and a `thinData` flag (fewer than N units, configurable). **All arithmetic here, in
   code.** This table is what Alpha judges; it should fit in one prompt for a layer.
2. **The six read-only tools** from plan section 5 (`get_band_performance`, `get_cost_changes`,
   `get_stock_health`, `get_past_decisions` (empty until `AgentDecision` exists),
   `get_elasticity_summary`, `get_current_config`), each returning compact JSON from the views.
3. **`query_data(sql)`**, with its guardrails in code:
   - a database user without a login, `agent_reader`, granted SELECT on schema `agent` only
     (ownership chaining lets the views read their tables); every query runs as that user;
   - parse with `Microsoft.SqlServer.TransactSql.ScriptDom` and accept exactly one SELECT statement
     (no `INTO`, no `EXEC`, no other statements);
   - 30-second timeout, at most 500 rows returned (say when it was cut off);
   - a `AgentQueryLog` row per query (agent, SQL, rows, duration, error);
   - a SQL error goes back to the model as the tool result.
4. **Expose the tools to Claude Code as an MCP server** (stdio, a small console project in the
   solution, e.g. `PricingTool.AgentTools`, using the official C# MCP SDK; check its current API
   first). `ClaudeCodeClient` passes it with `--strict-mcp-config --mcp-config` and allows only these
   tools. Prove it with one real call: "how many units did layer 4 sell in September 2026 by band?"
   answered through `query_data`, logged in `AgentQueryLog` and `AiCallLog`.
5. **Alpha's system prompt** = a rules file (its role, the decision schema, the limits it must stay
   inside, "do the reasoning, not the arithmetic") + `agent-data-dictionary.md`. Keep it in the repo
   under `PricingTool.Agent/Prompts/`.

**Not in this block:** the decision loop and `SafetyLimits`, and publishing; they're the next
blocks in section 6, so start them as soon as this one is done.

**Done when:** the scorecard builds for layers 1 and 4 in seconds; the tools and `query_data` work
through MCP in one real Claude Code call; a bad query (an `UPDATE`, two statements, a 10-second
`WAITFOR`) is rejected or timed out with a clear message; logs written; `hours.md` updated.
