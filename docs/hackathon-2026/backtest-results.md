# Backtest results (30 Oct 2026, build session; windows settled on 31 Oct after the AI-judge dry runs)

What v2 would have added on Kosovo's two layers, lever by lever, on booked sales lines (booked COGS, EUR VAT
excluded). **Every lever on the calculator's era, Aug 1 – Sep 30 2026** (monthly average below), with Oct 1–15 as an
out-of-sample check: framing A's baseline is today's process, the calculator with its pushes. Lever 3's low case leaves
out August (one cost event, below). Commands: `PricingTool.AgentTools backtest-snapshot` (the stored run,
`ai.BacktestSnapshot`, shown on the Backtest page with a CSV download; it goes back to Oct 2025 for context), or
`backtest-levers 1 4 [--from --to] [--detail]` and `backtest-campaigns 1 4`. All gains are **estimated** unless marked
measured; elasticity cases −0.5 / −1.0 / −2.0 (assumed), the low case (−2.0) is Finance's.

## Monthly gain, Aug–Sep average (EUR a month)

| # | Lever | GjirafaMall KS: central (low / high) | Gjirafa50 KS: central (low / high) | Basis |
|---|---|---|---|---|
| 1 | Loss-sale block | 77 | 120 | measured loss, estimated catch |
| 2 | Per-category floors | 0 | 4 (2 / 4) | estimated; Alpha's 8 rules, Gj50 only; in-sample |
| 3 | Cost reaction | 1,334 (134 / 1,504) | 212 (193 / 229) | measured lag, estimated recovery; the low case is September only |
| 4 | Coverage MK / AL | — | — | the four MK/AL layers: 1,325 (560 / 1,654), estimated: the calculator's Kosovo rate on months without a push |
| 5 | Cadence | 396 (260 / 474) | 0 | measured run → push gaps only |
| 6 | Learning | 0 | — | as built; the plan's plain rule: −614 |
| 7 | Campaign planning | 578 | 205 | estimated, 12 months of campaigns; uplift curve leave-one-out (low: half its slope, high: twice) |

**With lever 4 (MK/AL): about €4.3k a month central, €2.0k low, €5.8k high (E8: €4,251 / €2,049 / €5,806; snapshot #3 of 31 Oct, E20 recomputed).** Against the €10k bar for band 5
this is far short; the levers add to the rollout of the calculator's tested effect (framing B), they don't replace it.
The financial sheet then leaves out the holdout categories' share (about 11 % of the changed prices), which the backtest
doesn't. **What was tried and dropped:** after dry run 1, levers 1 and 3 were averaged over 12 months (Oct 2025 – Sep
2026) to stop August's one event counting every month; dry run 2 showed that scored them against months before the
calculator, a baseline that no longer exists (lever 1 rose from €0.2k to €0.9k on those months). The low case now
answers the one-event point instead.

## What each number rests on

- **Lever 4.** MK/AL tool-scope gross profit per month (`hk.SalesLinesMkAl`, loaded by the data session: Mall own
  vendors, no outlet, no Apple/Starlink, booked COGS, EUR at the shop's rate of the load day; MKD is pegged, ALL at
  today's rate): €16.4k / €25.7k / €20.2k in Jul / Aug / Sep on the months without a push. A month counts as pushed when
  a public, all-platform discount set of 50,000+ products started in it (`hk.PushDatesMkAl`): Gj50 AL in July, Mall AL
  in September, all four in October (21 Oct). The calculator's measured Kosovo gain as a share of Kosovo's gross profit
  (€180.4k a month, Aug–Sep): low €4.4k = 2.4 % (same-weeks control, one run per KS layer), mid €10.4k = 5.8 %
  (before/after), high €13.0k = 7.2 % (season-adjusted; quoted from the main repo). Low and mid are recomputed from this
  repo's data (`evidence/e20-test-phase.sql`). The plan's "≈ 10 %" is above all three measurements. `backtest-coverage`.
- **Lever 1.** Cross-dock lines on normal days sold under their booked cost, e.g. €394 / €2,671 / €250 a month on
  Mall (Aug / Sep / Oct 1–15), €402 / €539 / €175 on Gj50 (measured). Counted only where the cost the SKU last
  sold at was already above the price, i.e. the tool knew: 4–37 % of the loss in Aug–Sep (18–81 % on Mall in
  Nov–Jul, before the calculator lifted most of the known-cost losses). It assumes a blocked loss sale costs nothing else (no lost basket). Most of the loss arrives with a
  cost that rose at that very sale; whether CostWatcher sees it first depends on how fast the replica's cost
  refreshes (a go-live question).
- **Lever 2.** Where the agent's per-category floor is above the hand-set floor of that day (rebuilt from the
  band edits in the audit log), a line under it is lifted to it, never above the line's pre-discount price or
  the SKU's highest price of the 60 days before (assumed ceiling). Alpha's 8 rules (Gj50, set 30 Oct) each moved
  a band's floor by one point (the step limit), but they sit 1–3 points above the hand-set floors of Aug–Sep, which
  were lower then; few lines sat under them. *In-sample:* the rules were set with data through October.
- **Lever 3.** A line whose unit cost is more than 5 % above the SKU's 30-day median while its price moved less
  than 1 %, priced back to its prior margin from the day after the jump was seen. **August is one event**,
  checked: in the week of 3 August a third of Mall's lines carried a booked cost more than 5 % above July
  (673 lines against about 180 in a normal week) and prices followed about two weeks later (e.g. SKU 892712lt,
  cost €17.44 → €21.81 on 4–5 Aug, sold at €22.03 net until 19 Aug). So the low case is September only.
  For context, before the calculator Mall's lever 3 ran €93–736 a month at e −1 (Nov–Jul), €2,467 in August.
- **Lever 5.** Sales on full days between a run finishing and its approval going live, at the price the engine
  had already computed. Lags were 0–3 days per push. What the engine would have proposed *between* pushes
  can't be scored: those proposals weren't kept. Gj50's October gap was mostly markdowns (−€123 to −€218).
- **Lever 6.** Approval 12 (Mall, 6 Aug) judged per category, GP/day 28 days after vs before, season-adjusted
  with 2025. The plan's plain rule ("roll back where the category didn't gain": Shtëpi ×0.81, Sport ×0.96)
  would have **cost** €1.0–1.3k in September: the raised SKUs earned more even where the category's GP/day
  fell. The outcome tracker as built rolls back only a drop of 20 % that is worse than its no-change windows;
  nothing here qualifies, so €0.
- **Lever 7.** Each finished campaign replayed on its own lines with the planner setting each SKU's depth,
  keeping the campaign as deep on average as it was (a replay free to drop the discount claimed the event lift
  for free: Gj50 Black Friday 2024 €26k → €73k, rejected). Units from the uplift curve refitted without the
  campaign scored. Median gain per campaign day × campaign days of the last 12 months: Mall €210 × 33,
  Gj50 €112 × 22. About 2–8 % of a campaign's GP. With the curve at half its slope €703 a month, at twice it
  €1,744 (E7's low and high).

## The campaign planner's own estimate, checked

Replaying each finished campaign with the planner's arithmetic (rate from the 28 normal days ending 21 days
before the start): a median 44 % of a Mall campaign's GP (72 % on Gj50) comes from SKUs that had **no sales**
in the weeks before. On whole campaigns the actual GP was ×0.45–×3.90 the arithmetic (leave-one-out). Only 3
usable Mall campaigns have an event lift from another window of their type, too few for a factor, so plans
judge goals on the raw arithmetic and show the in-sample factor (×1.22 Mall, ×1.44 Gj50) for comparison only.
Black Friday 2026's lift (×5.16) rests on Black Friday 2024 alone (2025 is marked not representative).

## The Për ty reproduction test (`reproduce-per-ty --test`)

The user's brief of 6 Oct (Për ty, GjirafaMall KS, 14–18 Oct, GM3 ≥ 10 %, about 14.9 % effective margin), with data cut
off at 7 Oct: candidates from the booked sales lines of 26 Aug – 22 Sep (the engine's runs of then weren't kept; stock
unknown), the depth fit, event lift and calibration as of 7 Oct; the real Alpha and Nova, records in the test database.

| | Margin | Share of normal margin kept | GP, Mall's own SKUs |
|---|---|---|---|
| Hand plan (6 Oct) | 15.1 % forecast | 74 % (15.1 / 20.5, its notes) | — |
| Agents, first run (30 Oct) | 19.0 % expected | 88 % (our base) | €43.0k expected |
| Agents, after the fix (30 Oct) | 14.9 % expected | 69 % on our base; 67 % on the hand plan's base | €37.8k expected |
| What happened under the hand plan (measured, provisional) | 14.8 % | — | €39.6k |

"What happened" scores the hand plan's 15.1 % forecast (it was 0.3 pt off); the agents' plan is a different plan and
never ran, so it isn't scored by it.

**Verdict: FAIL on both bases.** The margin is within ±0.5 pt of 15.1 %; the share kept is under the 70–80 % required:
69 % on our base (the SKUs in scope, normal-day revenue-weighted, rate window 26 Aug – 22 Sep: 21.6 %), 67 % on the hand
plan's own base computed from today's data (Mall's own SKUs, booked margin, 6 Sep – 6 Oct: 22.3 %). The hand plan's
20.5 % (computed 7 Oct by an ad-hoc analysis, vendor scope and Gjirafa50-listed flag as of that day) can't be reproduced
exactly from hk; on hk's tool scope the same window gives 22.3 %. Cause of the difference not established. The bar stays
where it is.

The first run showed a real gap: no strategy spent a margin floor on volume (all stayed at 19–21 %), and the rules
pointed a margin goal at max-gross-profit. The planner now has spend-to-margin, and the rules call a margin goal on a
main calendar campaign a floor to spend. Gjirafa50-listed products were 32 % of Mall KS revenue at 14.1 % booked margin
(6 Sep – 6 Oct; the 7 Oct notes: 37 % at 13.3 %), measured; planning leaves them out, as the hand plan did, but the
agents didn't find this themselves (planning gives them no data tools), so the bonus isn't met.

## Not done yet

- A results page and CSV download (Agent tab block).
