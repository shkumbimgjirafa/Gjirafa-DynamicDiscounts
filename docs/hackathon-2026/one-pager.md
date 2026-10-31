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
cost or a 3 % margin on any SKU). An AI run checker confirms, escalates or fixes flagged prices, and can't lift a
rule's block; a cost watcher checks costs every 15 minutes; an outcome tracker measures every applied change and rolls
back what hurts; a publisher writes the shop's NOP discounts itself, with audit and rollback, and **Run week** runs the
whole loop for a layer with one press; a backtest; a chat. **The AI decides, code enforces:** no model output changes a
price, band or discount without passing limits written in C#. During the hackathon the publisher writes only to a
sandbox copy of the shop's price tables; going live is configuration and a DBA's grant (`GO-LIVE.md`), never yet run
against the live shop. A discount can only lower a price, so a raise is written as the store's regular price: on 31 Oct's
Run weeks 57 % of the two KS layers' checked prices reached the sandbox shelf (909 of them raises); a cheaper team deal held the rest.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports with the holdout left out); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin 14.9 % within bounds, but
69 % of normal margin kept against 70-80 %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email and the start-up task aren't live; the money ledger has measured nothing yet.

**Money (12 months, both framings kept apart).** Framing A counts only what this build adds, backtested walk-forward on
booked sales lines: €2,049 / €4,251 / €5,806 a month at full scope (E8,
estimated, every lever on the calculator's era, Aug–Sep 2026), short of the €12.2k a month that the top
net-benefit band needs with these costs. Most of it comes from automation (the loss block, cost reaction, daily checked
prices, MK/AL coverage), not from the model's own floor choices, which the backtest scores at €4
a month (E2); the holdout categories, about 11% of the changed prices, are left out of the benefit. Framing B adds the rollout of the calculator's test phase: €4,402 a month
measured with a same-weeks control (one controlled run per layer), €10,409 before/after and €13,000
season-adjusted, neither controlled (E20; the low and mid cases recomputed from this repo's data); it is the non-AI calculator's effect, which the registration did not claim as
past benefit, and B counts only the next 12 months of the automated system. The year ramps up: built in month 1, a
2-month pilot at 25% of the benefit, then full scope (9.5 full-benefit months). Costs:
€8,918 one-time (hackathon hours at €50, 160 hours to production including code review, the build
sessions' AI usage of $272.70 API-equivalent, E18) and €6,905 running (AI
€67 a month from the measured cost per call, E19; maintenance and review hours), €15,822 in all.
Which framing counts is the organisers' call.

| Year 1 | A · Low | A · Mid | A · High | B · Low | B · Mid | B · High |
|---|---|---|---|---|---|---|
| Extra gross profit a month at full scope | €1,824 | €3,783 | €5,167 | €5,741 | €13,047 | €16,737 |
| Net benefit (9.5 full-benefit months − all costs) | €1,502 | €20,120 | €33,268 | €38,721 | €108,128 | €143,183 |
| ROI | 9% | 127% | 210% | 245% | 683% | 905% |
| Payback (months) | 10.8 | 5.7 | 4.8 | 4.5 | 3.3 | 3.1 |
| Track A score (/100; Evidence and Works are our own ratings) | 40 | 66 | 68 | 68 | 84 | 84 |

**Biggest unproven assumptions.** That the prices reach the shelf: levers 1, 3 and 5 earn mostly by raising prices. The
publisher now writes raises as the regular price, but only on the sandbox so far, and a cheaper team deal still wins
until a person supersedes it (43 % of the checked prices on 31 Oct). That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change:
the backtest scores changed prices at elasticity −2 / −1 / −0.5 (low / mid / high). Framing B also assumes the test
phase's rate holds once automated and extended to every layer. The pilot's holdout categories turn these into
measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
