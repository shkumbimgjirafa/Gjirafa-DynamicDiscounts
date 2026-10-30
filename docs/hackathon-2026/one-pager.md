# Pricing Tool v2: two AI agents that set prices, plan campaigns and publish discounts

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx).

**The problem.** The pricing calculator (v0.5, declared, running since July 2026) only reads and calculates. A person sets
every floor and target by hand, plans campaigns in spreadsheets, exports prices and creates the discounts in the shop.
So prices reach customers on a few manual dates, only in Kosovo, and a cost rise waits for the next push: cross-dock
lines alone sold €2,134 a month under their booked cost over the last 12 months (E14, measured).

**What's new (built this weekend, everything after the labelled import commit).** **Alpha**, the pricing agent, sets
each band's floor and target per main category every week. **Nova**, the campaign agent, keeps the calendar, writes
briefs with goals, and Alpha turns each into a plan that code computes (four strategies, charm-rounded, never under
cost). An AI run checker passes, holds or fixes flagged prices; a cost watcher reacts within 15 minutes; an outcome
tracker measures every applied change and rolls back what hurt; a publisher writes the shop's NOP discounts itself,
with audit and rollback; a backtest; a chat. **The AI decides, code enforces:** no model output changes a
price, band or discount without passing limits written in C#. During the hackathon the publisher writes only to a
sandbox copy of the shop's discount tables; going live is configuration (`GO-LIVE.md`).

**What runs, on real data.** Daily cycles on both Kosovo layers (engine runs of 130k and 511k SKUs, run checks, checked
exports with the holdout left out); Alpha's weekly pass applied 8 category rules within its limits; Nova planned Black
Friday 2026 against the goal a person set and the plan was held for that person because the arithmetic falls short of
it. **Not finished, and shown as such:** the Për ty reproduction test fails by one point (expected margin
14.9 % within bounds, but 69 % of normal margin kept against
70-80 %; E11); the campaign estimates aren't calibrated yet (E10); the Monday email and the start-up task aren't live.

**Money (12 months, both framings kept apart).** Framing A counts only what this build adds, backtested walk-forward on
booked sales lines: €2,954 / €4,255 / €4,822 a month (E8,
estimated), short of the €10.1k a month that the top net-benefit band needs with these costs. Framing B adds the rollout of the
calculator's test phase: €4,000 a month measured with a same-weeks control (a floor: one run on one layer),
€10,632 before/after and €13,000 season-adjusted, neither controlled (E20). B claims no past euro:
the registration said the calculator's measured Aug–Sep result is not claimed; B counts only the next 12 months of the
automated system, using that result as the rate. Costs: €6,620 one-time (hackathon hours at €50, 120 hours
to production, the build sessions' AI usage of $224.91 API-equivalent, E18) and
€4,506 running (AI €67 a month from the measured cost per call, E19; maintenance and review hours),
€11,126 in all. Which framing counts is the organisers' call.

| Year 1 | A · Low | A · Mid | A · High | B · Low | B · Mid | B · High |
|---|---|---|---|---|---|---|
| Extra gross profit a month | €2,954 | €4,255 | €4,822 | €6,954 | €14,887 | €17,822 |
| Net benefit (11 live months − all costs) | €21,368 | €35,679 | €41,916 | €65,368 | €152,631 | €184,916 |
| ROI | 192% | 321% | 377% | 588% | 1372% | 1662% |
| Payback (months) | 3.7 | 2.8 | 2.6 | 2.1 | 1.5 | 1.4 |
| Track A score (/100; Evidence and Works are our own ratings) | 66 | 74 | 86 | 78 | 90 | 94 |

**Biggest unproven assumption.** How demand answers a price change: the backtest scores changed prices at elasticity
−2 / −1 / −0.5 (low / mid / high), and framing B assumes the test phase's rate holds once automated and extended, while its mid and high
cases are before/after, not controlled. The pilot's holdout categories turn both into measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
