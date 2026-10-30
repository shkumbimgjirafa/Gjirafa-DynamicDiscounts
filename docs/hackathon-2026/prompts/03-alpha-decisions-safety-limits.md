# Prompt 03: Alpha decides, SafetyLimits enforce, decisions are stored

Detail for the block after prompt 02 (plan section 6, row "Pricing agent"). Read `CLAUDE.md`, then
plan section 5 from "`FloorTargetAgent`" through "`SafetyLimits`", and "The two agents' names".

**Build, committing after each step with `dotnet test` green:**

1. **`AgentDecision` table** (new migration): layer, band, category, action (`raise` / `lower` /
   `keep`), current and proposed floor %, current and proposed target %, confidence, reason,
   evidence references (scorecard row ids, `AgentQueryLog` ids), status (`Applied` / `Held` /
   `Rejected` / `Pending`), the limit that held or rejected it, `AiCallLog` id, timestamps,
   source (`Alpha` / `CostWatcher` / `Nova`).
2. **Alpha's decision pass, one Claude Code call per layer** (`opus`): input = the prompt-02
   scorecard + today's config; Alpha may use the MCP tools to investigate; output = one decision per
   scorecard row against a strict `--json-schema`. Rows flagged `thinData` are forced to `keep` by
   code, not by the prompt. Retry once on invalid JSON, then store the pass as failed and change nothing.
3. **`SafetyLimits`, in code, never overridable by the model** (plan section 5): floors and targets
   move at most ±1 pp per week (configurable); floor never below the layer's hard minimum; target
   never below the floor; never below cost outside dead stock; holdout categories (config, a few per
   layer) are never touched; malformed or out-of-range output rejected and logged; the kill switch
   (`Agent:Enabled`) stops everything at once.
4. **Applying a decision** in Autonomous mode: within limits → write the override (per-category
   floor/target if `BandCategoryRule` exists by then, else band level) and `AuditLogEntry`
   (category `Agent`); outside limits → `Held` to the exceptions inbox. Approve-first mode: everything
   `Pending`. Store the mode per layer in `ToolSettings` (Off / Approve-first / Autonomous).
5. **Time-sheet row** (plan "Time-sheet log"): every real Alpha pass writes a `TimeSheetEntry`
   (task type "Weekly floor and target review", what = layer + counts, minutes after = measured,
   proof = link to the pass page or decision ids). Create `TimeSheetTaskType` with `MinutesBefore`
   NULL; the user sets it. Don't log injected-test or backtest passes.
6. **Run it for real on layer 4 (Gjirafa50 KS):** one pass, then show the decisions table
   (applied / held / rejected with reasons) and the `AiCallLog` cost in `hours.md`.

**Tests:** limits (each rule, including a model that asks for +5 pp and gets held), thin data forced
to `keep`, holdout never touched, invalid JSON → retry → failed pass with no changes, kill switch.

**Done when:** a real pass on layer 4 produced stored decisions with reasons, the limits demonstrably
held at least one, nothing outside the limits was applied, tests green, `hours.md` updated.
