# Pricing Tool v2: the price engine, with two AI agents built around it

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx).

## The problem: four gaps in how we price

1. **No margin control.** Cross-dock products (bought from the supplier after the customer orders) can sell at a loss:
   €2,134 a month of gross profit lost on lines sold under their booked cost (last 12 months, E14, measured). Then
   come the thin margins. Margin floors exist for them, set by hand per price band, but prices often sit under them: in
   Aug–Sep **24 % of Kosovo's revenue sold under its floor and brought only
   6 % of the gross profit**, a 4 % margin against 19 % on the rest
   (Gjirafa50 alone: 30 % of its revenue, 10 % of its profit; E30, measured).
2. **Prices stand still.** Marketing sets the prices of general products for a whole month, the change takes a few days
   to go live, and in between there is little or no dynamic control, so a cost rise waits for the next list (the team's
   process, as stated by the user). The calculator ran by hand too: 8 regular price runs in Kosovo since July, and
   Gjirafa50 once went 78 days between them (E28, measured).
3. **Campaigns are slow and have no target.** Each one is planned by hand and takes a lot of time and effort, and it
   starts with no set goal for what it should earn or what margin it may give up (stated by the user).
4. **No dead-stock strategy.** Old stock is cleared in one-off batches a person starts: 4 on Gjirafa50 since July,
   none on a schedule (E28, measured).

## The answer: one price engine, run by AI agents

**The engine, run by its agents, promises €10,430 more gross profit every month.** The engine is the pricing calculator (v0.5,
declared, tested by hand since July): it prices every product of both Kosovo shops against a margin floor and a target
per price band, charm-rounded, never above the anchor, with dead stock on its own lane, marked down by age. In its
Aug–Sep test on the real shop, the products it repriced earned **€7,597 a month** more gross profit than untouched
products (E27, measured). Run every day by the agents, with what they add (the loss block, cost reaction, daily cadence,
campaign planning, raises that reach the shelf), it earns **€2,833 a month** more (estimated, E8).

## The agents and their crew

All built this weekend (everything after the labelled import commit).

| | In a few words | What it does | Fixes |
|---|---|---|---|
| **Alpha**, the pricing agent (AI) | *Floors that move* | Sets each band's floor and target per main category, every week, within limits in code | 1, 2 |
| **Nova**, the campaign agent (AI) | *Every campaign gets a goal* | Keeps the calendar and writes each brief with a goal; Alpha picks one of four plans code computes, charm-rounded, never under cost or a 3 % margin on any SKU | 3 |
| **Run checker** (AI) | *A second look* | Confirms, escalates or fixes the flagged prices of every run; it can't lift a rule's block | 1 |
| **Cost watcher** (code) | *A 15-minute reflex* | On 31 Oct it read the morning's 293 real cost moves within 0.7–9.1 minutes; no rise was raised: 102 of 156 rises left the price above its floor, the other 54 went to a person (E22) | 1, 2 |
| **Publisher** (code) | *Writes the discounts itself* | Writes the shop's NOP discounts, with audit and rollback | 2, 4 |
| **Run week** (code) | *One press* | Runs the whole loop for a shop: price, check, publish, read back | 2, 4 |
| **Outcome tracker** (code) | *Keeps what works* | Measures every applied change and rolls back what hurts (tested; no pushed change has completed its window yet) | all |

**The AI decides, code enforces.** No model output changes a price, band or discount without passing limits written in
C#; campaign terms a person gives in the chat (cancellations too) are the model's reading of the message, bounded by code
but not checked against the message itself. **One app, four tabs:** Run (the current run as live, its expected effect,
Alpha's account), Chat (the agents' activity and only what needs a person), Campaigns (the calendar, with Nova to talk
each campaign through: re-plan, cancel, a whole-shop campaign on both shops) and Proposals; plus a backtest. **Sandbox
first:** during the hackathon the publisher writes only to a sandbox copy of the shop's price tables; going live is
configuration, a DBA's grant, the shop's answers on its cache and `OldPrice`, and the owner's call on superseding
(`GO-LIVE.md`), never yet run against the live shop.

## What runs, and what isn't finished

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it.

**On the sandbox shelf.** A discount can only lower a price, so a raise is written as the store's regular price: on 31 Oct's
Run weeks 57 % of the two KS layers' checked prices reached the sandbox shelf (909 of them raises); the rest were held by cheaper discounts,
96.5 % of them the calculator's own October pushes the team imported, not team deals (1.5 % of the checked prices). The user then
switched on superseding only those, and on 31 Oct held back the margin trim's cuts (49,214 of 50,969 GjirafaMall and 25,390 of
26,604 Gjirafa50 checked prices then on the sandbox shelf). On 1 Nov the user let the cuts go out again: that day's Run weeks put
133,957 of 135,255 GjirafaMall and 32,225 of 33,219 Gjirafa50 checked prices on the sandbox shelf, all read back as written.

**Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin 14.9 % within bounds, but
69 % of normal margin kept against 70-80 %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email isn't built and the start-up task is disabled; setting a price on the
Proposals tab doesn't publish it; the money ledger has measured nothing yet.

## The money

**The claim (12 months): €10,430 a month at full scope** (year 1: net benefit €88,017, ROI
795%, payback 3.1 months).

**Where it comes from.** *The price engine, measured:* in the calculator's Aug–Sep test on the real shop, the products
it repriced earned €7,597 a month more gross profit than untouched products in the same shop and category in the same
weeks (E27; booked sales lines, booked COGS; the evidence file reruns it, and a placebo before the calculator's first
approval shows no such lift, E29). *The AI agents, estimated:* €2,833 a month more from the controlled pricing logic
the agents run: the loss block, cost reaction, daily cadence, campaign planning and raises that reach the shelf
(walk-forward backtest, E8). Together €10,430 a month. The engine was a test, run by hand; this build makes it
permanent and automatic, and the agents learn from the measured outcome of each change. *It runs by itself (on the
sandbox during the hackathon):* Run week on Gjirafa50 KS on Monday's fresh data, started ten minutes before the demo and
shown in it: the proposed prices, the check, the publish to the local sandbox with its rollback, the read-back, every
number traced to a query. The past months are not claimed; Aug–Sep is the evidence for the rate.

**The test, and why it counts.** The calculator (v0.5) ran only as a test, by hand, with no schedule (E28, measured from
the tool's approvals and the pushed discount sets): GjirafaMall KS on 21 Jul, 6 Aug, 3 Sep, 6 Oct and 19 Oct;
Gjirafa50 KS on 21 Jul, 7 Oct and 21 Oct, with dead-stock runs on 3 Aug, 19 Aug and 3 Sep; North
Macedonia and Albania had full pushes in July (Gjirafa50 AL), September (GjirafaMall AL) and on 21 Oct (all four). The
last run was 21 Oct; nothing has run since. Without this build it doesn't go live (assumed: stated by the
user, who ran the tests). The rate above was measured in Aug–Sep 2026 on the runs live then: GjirafaMall KS's of 21 Jul,
6 Aug and 3 Sep (with smaller floor and dead-stock sets) and Gjirafa50 KS's of 21 Jul with its dead-stock runs (E27).

**Coverage.** Each engine run prices every product of both Kosovo shops: GjirafaMall KS, 511,198 SKUs, and Gjirafa50
KS, 129,708 SKUs (runs #1111 and #1110, 1 Nov 2026, measured). Nothing goes to the live shop yet: the runs only
compute, and publishing writes to the sandbox. Live, it would price the same products, every category included.
North Macedonia and Albania are built in but not in the claim. **What makes it live-ready** is what this weekend built:
the two agents, the limits in code, the run checker, the cost watcher, the outcome tracker and the publisher with audit
and rollback; going live is configuration and a write login (`GO-LIVE.md`).

| Where the claim's benefit comes from | € a month | Label |
|---|---|---|
| The price engine, measured against untouched products (B0) | €7,597 | measured (E27) |
| Recovered leakage: raises that reach the shelf (lever 8) | €249 | estimated (E24) |
| Cadence: prices daily, not on a few manual dates (lever 5) | €396 | estimated (E5) |
| The other levers: loss block, category floors, cost reaction, learning, campaign planning (1, 2, 3, 6, 7) | €2,188 | estimated (E1–E3, E6, E7) |
| **The claim, at full scope** | **€10,430** | |
| Hours saved | not counted | hours, never euros |

The levers come mostly from automation, not from the model's own floor choices (lever 2, €2 a
month, E2). GjirafaMall lines are Gjirafa's own vendors only, at booked cost: no marketplace commission applies; cancelled
orders are excluded and returns after delivery are not netted out. The year ramps up: built in month 1, a
2-month pilot at 25% of the benefit, then full scope (9.5 full-benefit months); payback comes from that ramp
(the pilot's assumptions are not tuned to cross a line). Costs:
€4,159 one-time (hackathon hours at €50, 40 hours to production including code review, the build
sessions' AI usage of $764.23 API-equivalent, E18) and €6,909 running (AI
€67 a month from the measured cost per call, E19; maintenance and review hours), €11,068 in all.

| Year 1 | The claim |
|---|---|
| Extra gross profit a month at full scope | €10,430 |
| Net benefit (9.5 full-benefit months − all costs) | €88,017 |
| ROI | 795% |
| Payback (months) | 3.1 |

## The risk, and the ask

**Biggest unproven assumption.** That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change:
the levers score changed prices at elasticity −1 (assumed). The publisher writes raises as the regular price, on the
sandbox so far. The pilot's money ledger turns these into measurements, each push against the shop's products it didn't
change.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); every category priced, with the money ledger measuring each push against the
shop's products it didn't change. That makes the go/no-go measured, not estimated.
