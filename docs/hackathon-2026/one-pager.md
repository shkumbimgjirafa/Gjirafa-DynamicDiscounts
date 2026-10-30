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
cost or a 3 % margin floor). An AI run checker confirms, escalates or fixes flagged prices, and can't lift a rule's
block; a cost watcher reacts within 15 minutes; an outcome tracker measures every applied change and rolls back what
hurt; a publisher writes the shop's NOP discounts itself, with audit and rollback; a backtest; a chat. **The AI decides,
code enforces:** no model output changes a price, band or discount without passing limits written in C#. During the
hackathon the publisher writes only to a sandbox copy of the shop's discount tables; going live is configuration
(`GO-LIVE.md`), never yet run against the live shop.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports with the holdout left out); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin 14.9 % within bounds, but
69 % of normal margin kept against 70-80 %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email and the start-up task aren't live; the money ledger has measured nothing yet.

**Money (12 months, both framings kept apart).** Framing A counts only what this build adds, backtested walk-forward on
booked sales lines: €2,944 / €4,186 / €5,630 a month at full scope (E8,
estimated; levers 1 and 3 over 12 months, the others over Aug–Sep), short of the €12.2k a month that the top
net-benefit band needs with these costs. Most of it comes from automation the agents run (the loss block, cost reaction,
daily publishing, MK/AL coverage), not from the model's own floor choices, which the backtest scores at
€4 a month (E2). Framing B adds the rollout of the calculator's test phase: €4,000 a month
measured with a same-weeks control (one run on one layer), €10,632 before/after and €13,000
season-adjusted, neither controlled (E20); it is the non-AI calculator's effect, which the registration did not claim as
past benefit, and B counts only the next 12 months of the automated system. The year ramps up: built in month 1, a
2-month pilot at 25% of the benefit, then full scope (9.5 full-benefit months). Costs:
€8,888 one-time (hackathon hours at €50, 160 hours to production including code review, the build
sessions' AI usage of $243.50 API-equivalent, E18) and €6,904 running (AI
€67 a month from the measured cost per call, E19; maintenance and review hours), €15,793 in all.
Which framing counts is the organisers' call.

| Year 1 | A · Low | A · Mid | A · High | B · Low | B · Mid | B · High |
|---|---|---|---|---|---|---|
| Extra gross profit a month at full scope | €2,944 | €4,186 | €5,630 | €6,944 | €14,818 | €18,630 |
| Net benefit (9.5 full-benefit months − all costs) | €12,175 | €23,974 | €37,692 | €50,175 | €124,978 | €161,192 |
| ROI | 77% | 152% | 239% | 318% | 791% | 1021% |
| Payback (months) | 6.9 | 5.4 | 4.5 | 4.1 | 3.2 | 3.1 |
| Track A score (/100; Evidence and Works are our own ratings) | 54 | 66 | 68 | 76 | 84 | 84 |

**Biggest unproven assumptions.** That the past year's pattern repeats (levers 1 and 3 rest on 12 months, levers 2, 5 and
6 on two), and how demand answers a price change: the backtest scores changed prices at elasticity −2 / −1 / −0.5 (low /
mid / high). Framing B also assumes the test phase's rate holds once automated and extended to every layer. The pilot's
holdout categories turn these into measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
