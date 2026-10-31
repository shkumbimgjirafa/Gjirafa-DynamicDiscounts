# Pricing Tool v2: two AI agents that set prices, plan campaigns and publish discounts

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx).

**The problem.** The pricing calculator (v0.5, declared, running since July 2026) only reads and calculates. A person sets
every floor and target by hand, plans campaigns in spreadsheets, exports prices and creates the discounts in the shop.
So prices reach customers on a few manual dates, mostly in Kosovo (North Macedonia and Albania got a push in some months
only), and a cost rise waits for the next push: cross-dock
lines alone sold €2,134 a month under their booked cost over the last 12 months (E14, measured). In Aug–Sep
€14,705 a month of gross profit went under the hand-set floor (E21), but it isn't one leak:
€2,954 of it is allowed or not a price (dead stock, vouchers), €4,238 is GjirafaMall SKUs that take
Gjirafa50's price, and the rest sits where the agents' levers work (loss sales, cost reaction, days without a calculator
price).

**What's new (built this weekend, everything after the labelled import commit).** **Alpha**, the pricing agent, sets
each band's floor and target per main category every week. **Nova**, the campaign agent, keeps the calendar, writes
briefs with goals, and Alpha turns each into a plan that code computes (four strategies, charm-rounded, never under
cost or a 3 % margin on any SKU). An AI run checker confirms, escalates or fixes flagged prices, and can't lift a
rule's block; a cost watcher checks costs every 15 minutes (on 31 Oct it read the morning's 293 real cost moves within
0.7–9.1 minutes of the refresh, raised none because no rise put a price we set under its floor, and held
54 for a person: 52 under the floor at a cheaper deal, of them 3 a team deal and the rest the calculator's own earlier push or an app-only deal, 2 with a floor above the shelf; E22); an outcome tracker is built to measure every applied change and roll
back what hurts (tested; no pushed change has completed its window yet); a publisher writes the shop's NOP discounts itself, with audit and rollback, and **Run week** runs the
whole loop for a layer with one press; a backtest; a chat. **The AI decides, code enforces:** no model output changes a
price, band or discount without passing limits written in C#. During the hackathon the publisher writes only to a
sandbox copy of the shop's price tables; going live is configuration, a DBA's grant, the shop's answers on its cache and
`OldPrice`, and the owner's call on superseding (`GO-LIVE.md`), never yet run
against the live shop. A discount can only lower a price, so a raise is written as the store's regular price: on 31 Oct's
Run weeks 57 % of the two KS layers' checked prices reached the sandbox shelf (909 of them raises); the rest were held by cheaper discounts,
96.5 % of them the calculator's own October pushes the team imported, not team deals (1.5 % of the checked prices). Superseding only those, 98.3 % reached it.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports with the holdout left out); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin 14.9 % within bounds, but
69 % of normal margin kept against 70-80 %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email and the start-up task aren't live; the money ledger has measured nothing yet.

**Money (12 months). Our claim is framing B; framing A is the conservative cross-check.** Framing B is what the
automated system earns in the next 12 months: the calculator's measured rate (B0, E20: €4,402 a month measured
with a same-weeks control, one controlled run per layer; €10,409 measured before/after and €13,000
season-adjusted, neither controlled; the low and mid cases recomputed from this repo's data, the high quoted) plus the
levers this build adds: €5,694 / €12,965 / €19,291 a month at full scope
(low / mid / high), the holdout categories, about 11% of the changed prices, left out.

*"B0 is the non-AI calculator's money."* The calculator is declared existing code and part of the submitted system.
Framing B claims no past euro: it is the next 12 months of the automated system, with Aug–Sep 2026 as the measured
evidence for the rate. v2 is what delivers that rate at scale, because today's manual push loses part of it: in Aug–Sep
17–20 % of the units on SKUs an approval raised still sold at the old price, 70–84 % in late October (E23, measured);
the days between a run and its push cost €396 a month (E5, estimated); the MK/AL months without a
push miss €1,325 a month (E4, estimated).

Framing A, the cross-check, counts only the levers, backtested walk-forward on booked sales lines:
€1,996 / €4,158 / €8,675 a month at full scope before the holdout share
(E8, estimated, every lever on the calculator's era, Aug–Sep 2026), short of the €12.2k a month that the top net-benefit band needs with
these costs. Most of it comes from automation (the loss block, cost reaction, daily checked prices, MK/AL coverage,
raises that reach the shelf: €249, E24), not from the model's own floor choices, which the backtest
scores at €2 a month (E2). The year ramps up: built in month 1, a
2-month pilot at 25% of the benefit, then full scope (9.5 full-benefit months), so payback can't
fall much under three months at these rates (the pilot's assumptions are not tuned to cross a line). Costs:
€9,039 one-time (hackathon hours at €50, 160 hours to production including code review, the build
sessions' AI usage of $393.76 API-equivalent, E18) and €6,921 running (AI
€68 a month from the measured cost per call, E19; maintenance and review hours), €15,959 in all.
Which framing counts is the organisers' call.

| Year 1 | B · Low | B · Mid | B · High | A · Low | A · Mid | A · High |
|---|---|---|---|---|---|---|
| Extra gross profit a month at full scope | €5,694 | €12,965 | €19,291 | €1,776 | €3,701 | €7,721 |
| Net benefit (9.5 full-benefit months − all costs) | €38,136 | €107,205 | €167,303 | €917 | €19,196 | €57,388 |
| ROI | 239% | 672% | 1048% | 6% | 120% | 360% |
| Payback (months) | 4.5 | 3.3 | 3.1 | 11.2 | 5.9 | 4.0 |
| Track A score (/100; Evidence and Works are our own ratings) | 68 | 84 | 84 | 40 | 66 | 76 |

**Biggest unproven assumptions.** That the calculator's measured rate holds once automated and extended to every layer
(framing B rests on it; only its low case had a control, one run per layer). That the prices reach the shelf: levers 1, 3 and 5 earn mostly by raising prices. The
publisher now writes raises as the regular price, but only on the sandbox so far, and a cheaper discount it didn't write still
wins until a person supersedes it (43 % of the checked prices on 31 Oct, almost all the calculator's own earlier pushes).
In today's process 17–20 % of the units on SKUs an approval raised still sold at the old price in Aug–Sep, 70–84 % in late
October (E23); the levers' gain on lines sold under a team deal (€378 a month) counts only in the high case. That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change:
the backtest scores changed prices at elasticity −2 / −1 / −0.5 (low / mid / high). The pilot's holdout categories turn
these into measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
