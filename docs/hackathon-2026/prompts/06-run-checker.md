# Prompt 06: the run checker (the check Mergca offered to review)

Detail for plan section 6, row "Run checker". Read `CLAUDE.md` and plan section 5 "`RunChecker`".

Mergca's guidance for this exact check (Fri 30 Oct), which the build follows:
- **Fixed rules in a rules file** passed as the system prompt: margin floor and target per band,
  what counts as a **block** and what is only a **warning**.
- **One strict JSON answer per run**, shaped like
  `{"run_id": …, "verdict": "pass|warn|block", "violations": [{"sku": …, "band": …, "new_price": …, "margin_pct": …, "floor_pct": …, "severity": "warn|block", "reason": …}]}`.
  Our code checks it parses (and matches the schema); invalid → retry once → then **block the run**.
- **Arithmetic in our code;** Claude gets only the rows that fail a rule or sit near the floor, and
  gives the judgement and the explanation. One call per run (`sonnet`), never one per product.
- Stock, cost and price data only; no customer data.

**Build, committing after each step with `dotnet test` green:**

1. **Rule flags in code** on an engine run's proposals (plan list): below cost; below the band floor
   (outside the dead-stock lane); cost moved > 5% since the previous dataset; |change| > 20%; not on
   the layer's charm ending; Mall vs Gj50 disagree on a shared SKU; wrong currency scale; above
   max(anchor, current). Plus "near the floor" (within 1 pp). Each flagged row carries the numbers
   already computed (price, cost, margin %, floor %, band, change %, stock).
2. **`PricingTool.Agent/Prompts/run-checker-rules.md`:** the fixed rules, which flags are a `block`
   (below cost, wrong currency scale, above the ceiling, a price ×10-type jump) and which a `warn`, and
   what the model may decide: confirm a block, downgrade to warn with a reason, or recommend a fixed
   price (which code re-checks against the same rules before using it).
3. **One Claude Code call per run** with the flagged rows (cap the batch; if a run has more flagged
   rows than the cap, send the highest-impact ones and mark the rest `warn` by rule, saying so).
   Verdict `block` → the run's export and publish are stopped; `warn` → they go ahead without the
   warned rows; `pass` → everything goes.
4. **`AgentRunCheck`** stores the verdict, each violation, the call id and the duration;
   a `TimeSheetEntry` for each real check (task type "Pre-publish check of a pricing run").
5. **Test cases:** real flagged rows from a real run, plus injected, labelled rows (a price ×10, a cost
   ×62, an un-charmed price, below cost) **in the test database only**.
6. **For Mergca's review:** after the first real check, write
   `docs/hackathon-2026/mergca-review/run-checker.md` with the exact `claude -p` command line (no
   token, no email), the rules file, the JSON schema, and one real sample output (SKUs and prices are
   fine; nothing personal). The user sends it to Mergca.

**Done when:** a real run of layer 4 was checked in one call with a stored verdict and violations,
a block demonstrably stopped an export in the test database, the review file exists, tests green,
`hours.md` updated.
