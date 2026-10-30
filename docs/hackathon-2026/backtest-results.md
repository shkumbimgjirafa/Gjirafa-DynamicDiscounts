# Backtest results (first full pass, 30 Oct 2026, build session)

What v2 would have added on Kosovo's two layers, lever by lever, on booked sales lines (booked COGS, EUR VAT
excluded). Test window Aug 1 – Sep 30 2026 (monthly average below), Oct 1–15 as an out-of-sample check.
Commands: `PricingTool.AgentTools backtest-levers 1 4 [--detail]` and `backtest-campaigns 1 4`. All gains are
**estimated** unless marked measured; elasticity cases −0.5 / −1.0 / −2.0, the low case (−2.0) is Finance's.

## Monthly gain, Aug–Sep average (EUR a month)

| # | Lever | GjirafaMall KS: central (low) | Gjirafa50 KS: central (low) | Basis |
|---|---|---|---|---|
| 1 | Loss-sale block | 76 (76) | 120 (120) | measured loss, estimated catch |
| 2 | Per-category floors | 0 | 4 (3) | estimated; Alpha's 8 rules, Gj50 only |
| 3 | Cost reaction | 1,315 (1,025) | 212 (181) | measured lag, estimated recovery; one event |
| 4 | Coverage MK / AL | not measured | not measured | MK/AL sales lines aren't loaded |
| 5 | Cadence | 397 (261) | 0 (0) | measured run → push gaps only |
| 6 | Learning | 0 | — | as built; the plan's plain rule: −614 |
| 7 | Campaign planning | 578 | 205 | estimated, uplift curve leave-one-out |
| | **Total** | **2,365 (1,939)** | **541 (508)** | |

**Both layers: about €2.9k a month central, €2.4k in the low case.** Against the €10k bar for band 5 this is
far short. Lever 4's rough size from the plan (€2–2.5k a month, *assumed*, not measured here) would bring it to
about €4.4–5.4k.

## What each number rests on

- **Lever 1.** Cross-dock lines on normal days sold under their booked cost: €394 / €2,671 / €250 a month on
  Mall (Aug / Sep / Oct 1–15), €402 / €539 / €175 on Gj50 (measured). Counted only where the cost the SKU last
  sold at was already above the price, i.e. the tool knew: 4–37 % of the loss. Most of the loss arrives with a
  cost that rose at that very sale; whether CostWatcher sees it first depends on how fast the replica's cost
  refreshes (a go-live question).
- **Lever 2.** Where the agent's per-category floor is above the hand-set floor of that day (rebuilt from the
  band edits in the audit log), a line under it is lifted to it, never above the line's pre-discount price or
  the SKU's highest price of the 60 days before (assumed ceiling). Alpha's 8 rules (Gj50, set 30 Oct) raise
  floors 1–3 points; few lines sat under them. *In-sample:* the rules were set with data through October.
- **Lever 3.** A line whose unit cost is more than 5 % above the SKU's 30-day median while its price moved less
  than 1 %, priced back to its prior margin from the day after the jump was seen. **August is one event**,
  checked: in the week of 3 August a third of Mall's lines carried a booked cost more than 5 % above July
  (673 lines against about 180 in a normal week) and prices followed about two weeks later (e.g. SKU 892712lt,
  cost €17.44 → €21.81 on 4–5 Aug, sold at €22.03 net until 19 Aug). Without it Mall's lever 3 is about
  €150–200 a month.
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
  Gj50 €112 × 22. About 2–8 % of a campaign's GP. Sensitivity (curve ×0.5 / ×2) per campaign in the command.

## The campaign planner's own estimate, checked

Replaying each finished campaign with the planner's arithmetic (rate from the 28 normal days ending 21 days
before the start): a median 44 % of a Mall campaign's GP (72 % on Gj50) comes from SKUs that had **no sales**
in the weeks before. On whole campaigns the actual GP was ×0.45–×3.90 the arithmetic (leave-one-out). Only 3
usable Mall campaigns have an event lift from another window of their type, too few for a factor, so plans
judge goals on the raw arithmetic and show the in-sample factor (×1.22 Mall, ×1.44 Gj50) for comparison only.
Black Friday 2026's lift (×5.16) rests on Black Friday 2024 alone (2025 is marked not representative).

## Not done yet

- Lever 4 (MK/AL): needs MK/AL sales lines loaded (a data-session task) and the MK/AL push dates of Jul–Oct.
- The Për ty reproduction test (agents alone vs the hand plan of 6 Oct).
- A results page and CSV download (Agent tab block).
