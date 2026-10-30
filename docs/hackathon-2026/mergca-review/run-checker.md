# Run checker: for Mergca's review

The pre-publish check of a pricing run, built on Mergca's guidance of Fri 30 Oct: fixed rules in a
rules file as the system prompt, one strict JSON answer per run, our code checks it (invalid → one
retry → block), arithmetic in our code, only flagged rows sent, one call per run (Sonnet), stock,
cost and price data only.

Code: `PricingTool.Agent/Checker/` (`RunCheckRules.cs`: the rules; `RunChecker.cs`: the call and the
verdict; `CheckedExport.cs`: the gate). Tests: `PricingTool.Tests/RunCheckRulesTests.cs`,
`RunCheckerTests.cs`.

## The exact call

One `claude -p` process per run (Claude Code 2.1.314, through the team's subscription login on this
machine; no API key, no token on the command line):

```
claude -p --output-format json --model claude-sonnet-5-5
  --system-prompt-file "<app>\Prompts\run-checker-rules.md"
  --setting-sources "" --tools "" --strict-mcp-config --disable-slash-commands
  --no-session-persistence --max-budget-usd 2
  --json-schema '{"type":"object","properties":{"run_id":{"type":"integer"},"verdict":{"enum":["pass","warn","block"]},"violations":{"type":"array","items":{"type":"object","properties":{"sku":{"type":"string"},"band":{"type":"string"},"new_price":{"type":"number"},"margin_pct":{"type":["number","null"]},"floor_pct":{"type":["number","null"]},"severity":{"enum":["warn","block"]},"reason":{"type":"string","maxLength":600},"fix_price":{"type":["number","null"]}},"required":["sku","band","new_price","margin_pct","floor_pct","severity","reason","fix_price"],"additionalProperties":false}}},"required":["run_id","verdict","violations"],"additionalProperties":false}'
```

- Working directory: `%LOCALAPPDATA%\PricingToolAgent\claude-runtime` (outside the repo; refused if a
  `CLAUDE.md` sits in it or above it).
- The prompt (the flagged rows) goes on **stdin**, never on the command line.
- Environment: the parent's `CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables are removed;
  `ENABLE_CLAUDEAI_MCP_SERVERS=false` (no claude.ai connectors), `MCP_CONNECTION_NONBLOCKING=false`.
- Isolation, checked with a real call on 2.1.314 (`ai.AiCallLog` #13): no built-in tools, no CLAUDE.md,
  memory, skills or MCP servers reach the model; Claude Code adds only the account e-mail, the date, a
  budget line and an environment block (1,275 input tokens in all). The call log redacts e-mails.

## What our code sends

Code evaluates every changed price of the run against the rules below, then sends the **at most 150**
highest-impact flagged rows (blocks first, then by size of the price move); rows over the cap keep the
rule's severity and the check says how many. One line per row:

```
sku|band|new_price|current|shelf|anchor|cost|margin_pct|floor_pct|change_pct|stock|units_30d|dead_stage|rule_severity|flags|engine_reasons
```

## What our code does with the answer

- The answer must parse and match the schema; otherwise one retry, then **the run is blocked**.
- Per row: the model may confirm a block, downgrade it to a warn (with a reason), raise a warn to a
  block, or propose `fix_price`. A fix is re-checked against every rule and used only if nothing
  above "info" fires; it is never applied on the model's word.
- The verdict that counts is computed by code from the rows: any block → **block** (export and publish
  stop); any warn → **warn** (the run goes ahead without those rows); else **pass**. The model's own
  verdict is stored next to it.
- Stored in `ai.AgentRunCheck` (verdict, counts, call id, duration) and `ai.AgentRunCheckRow` (each row's
  numbers, rule / model / final severity, reason, fix).

## The rules file (system prompt)

# Run checker: fixed rules

You check a pricing run before its prices are exported or published to the shop. A rules engine has
already priced every SKU; code has already flagged the prices that break a rule or sit near the floor,
and computed every number you see. You judge those flagged rows and explain each judgement.

## The rows

One row per flagged price: the new price, today's price, the shelf price, the anchor, the cost (PPTCV,
VAT included), the margin `(price - cost) / price`, the band's margin floor, the change against today,
local stock, units sold in the last 30 days, the dead-stock stage, the rule severity, the rule codes
that fired, and the engine's own reason codes.

## What the rules mean

**Block** (the whole run is stopped while any row stays a block):
- `below-cost`: the price is below cost, outside the dead-stock lane.
- `dead-stock-below-hard-floor`: dead stock priced under 40 % of cost (its lowest tier).
- `x10-jump`: the price is ten times today's price, or a tenth of it.
- `price-scale` / `cost-scale`: a price or a cost at the wrong currency scale (e.g. denars or lek in a euro
  layer: about ×62 or ×98).
- `above-ceiling`: the price is above max(anchor, today's price). Prices are never raised above that.
- `no-price`: zero or negative price.

**Warn** (the row is left out of the export; the rest of the run goes ahead):
- `below-floor`: margin below the band floor outside the dead-stock lane.
- `change-over-20`: the price moves more than 20 % against today's price; a person confirms these.
- `off-charm`: not on the layer's price-ending grid (Gjirafa50 euro prices end in .50 tiers, GjirafaMall
  euro prices in .99). Not raised where the engine left the price off the grid on purpose: held at
  an off-grid anchor (the ceiling), clamped at the dead-stock hard floor, or rounding skipped at a bound.
- `cost-moved`: the cost moved more than 5 % since the previous run.
- `mall-gj50-disagree`: GjirafaMall and Gjirafa50 price a shared SKU differently (Mall should follow Gj50).
- `no-cost`: no cost, so the margin can't be checked.

**Info** (never holds anything): `near-floor`, within 1 point above the floor.

Context that explains a flag and is not an error: dead-stock rows (`dead_stage` above None) may sit below
the floor and down to 40 % of cost by design; `DEAD_STOCK_HARD_FLOOR_CLAMPED` prices sit exactly on that
40 % line and are left unrounded on purpose; `SYNCED_TO_GJ50` means GjirafaMall took Gjirafa50's price;
`MARGIN_FLOOR_CLAMPED` means the engine lifted the price to the floor.

## What you decide, per row

Return exactly one violation per row you were given, with:
- `severity`: `block` or `warn`.
  - Confirm a block as `block`, or downgrade it to `warn` when the numbers show it is not an error
    (say which numbers in `reason`).
  - Keep a warn as `warn`, or raise it to `block` when the row looks like a data error that could reach
    customers.
- `reason`: one or two sentences a pricing manager can check, naming the figures.
- `fix_price`: a better price when you can name one (on the layer's grid, not below cost outside dead
  stock, not above max(anchor, today's price)); otherwise null. Code re-checks a fix against every rule
  and uses it only if it passes; it is never applied on your word alone.

Then the run's `verdict`: `block` if any row stays a block, `warn` if any row is a warn, else `pass`.
Code computes the verdict that counts from the rows; yours is recorded next to it.

## Do the reasoning, not the arithmetic

The numbers were computed by code. Quote them; don't recompute margins or percentages. Copy each `sku`,
`band`, `new_price`, `margin_pct` and `floor_pct` from the row as given.

Answer with the JSON only.

## The JSON schema (`run-check.schema.json`)

```json
{
  "type": "object",
  "properties": {
    "run_id": { "type": "integer" },
    "verdict": { "enum": ["pass", "warn", "block"] },
    "violations": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "sku": { "type": "string" },
          "band": { "type": "string" },
          "new_price": { "type": "number" },
          "margin_pct": { "type": ["number", "null"] },
          "floor_pct": { "type": ["number", "null"] },
          "severity": { "enum": ["warn", "block"] },
          "reason": { "type": "string", "maxLength": 600 },
          "fix_price": { "type": ["number", "null"] }
        },
        "required": ["sku", "band", "new_price", "margin_pct", "floor_pct", "severity", "reason", "fix_price"],
        "additionalProperties": false
      }
    }
  },
  "required": ["run_id", "verdict", "violations"],
  "additionalProperties": false
}
```

## One real sample answer

Engine run #1098 (Gjirafa50 KS, 30 Oct 2026), check #2: the first four of the 150 violations, verbatim
(SKUs and prices only):

```json
{
  "run_id": 1098,
  "verdict": "warn",
  "violations": [
    {
      "sku": "13245581mo",
      "band": "1,000+",
      "new_price": 2124.5,
      "margin_pct": 9.7,
      "floor_pct": 6,
      "severity": "warn",
      "reason": "Moves +32.8% vs today's 1599.50, over the 20% limit; today's price was below the floor and the new price has 9.7% margin vs 6.0% floor, under anchor 2241.50. Needs human confirmation.",
      "fix_price": null
    },
    {
      "sku": "600360414mo",
      "band": "1,000+",
      "new_price": 2324.5,
      "margin_pct": 9.3,
      "floor_pct": 6,
      "severity": "warn",
      "reason": "Moves +25.7% vs 1849.50; today's price was below floor, new margin 9.3% vs 6.0% floor, below anchor 2458.50. Confirm the jump.",
      "fix_price": null
    },
    {
      "sku": "5814744mo",
      "band": "250–500",
      "new_price": 569.5,
      "margin_pct": 16.4,
      "floor_pct": 6,
      "severity": "warn",
      "reason": "Moves +484.1% from 97.50, but today's price is far below cost 475.94; new price has 16.4% margin and is under anchor 575.50. Looks like a legitimate correction, but a person should confirm.",
      "fix_price": null
    },
    {
      "sku": "1189629mo",
      "band": "1,000+",
      "new_price": 1624.5,
      "margin_pct": 9.8,
      "floor_pct": 6,
      "severity": "warn",
      "reason": "Moves +38.9% vs 1169.50 after today's price fell below floor; margin 9.8% vs 6.0% floor, under anchor 1849.50.",
      "fix_price": null
    }
  ]
}
```

## Results on real data (measured)

| Check | Run | Changed prices | Flagged | Sent | Block | Warn | Fixed | Verdict | Time | Cost |
|---|---|---|---|---|---|---|---|---|---|---|
| #1 | #1098 | 44,577 | 5,989 | 150 | 0 | 5,961 | 2 | warn | 100 s | $0.28 |
| #2 | #1098 | 44,577 | 5,950 | 150 | 0 | 5,922 | 0 | warn | 87 s | $0.26 |

- Between the two checks one rule changed: check #1 held back 74 prices the engine had left off the
  price grid on purpose (held at an off-grid anchor, clamped at the dead-stock hard floor, or rounding
  skipped at a bound). The off-charm rule no longer fires on those.
- Almost every remaining warn is a change over 20 % against today's price, mostly lifting a price that
  was below the floor or below cost; the existing tool already asks a person to confirm those.
- Export after check #2: 38,655 prices exported, 5,922 held back.
- "cost-moved" was not evaluated: there is no earlier run with proposals on this layer yet.
- Cost is the API-equivalent figure Claude Code reports (`total_cost_usd`); calls are billed to the
  subscription.

## Tests

Each rule on its own; injected, labelled rows (a price ×10, a cost ×62, an off-grid price, a below-cost
price) in the test database only, where the block stops the export; the warn and fix paths; a fix that
breaks a rule is refused; downgrade and escalation; an invalid answer → block; the kill switch → block;
the cap.
