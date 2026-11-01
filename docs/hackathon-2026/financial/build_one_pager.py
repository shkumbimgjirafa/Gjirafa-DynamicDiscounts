"""Writes docs/hackathon-2026/one-pager.md from evidence/numbers.json and the recalculated financial sheet.

Every figure on the page is read from those two files (IDs in brackets), so the page, the sheet and HACKATHON.md can't
drift apart. Run after build_financial_sheet.py and excel_recalc.ps1 (the sheet must carry Excel's cached values):

    python -I docs/hackathon-2026/financial/build_one_pager.py
"""
import json
from pathlib import Path

from openpyxl import load_workbook

HERE = Path(__file__).resolve().parent
DOCS = HERE.parent
NUMBERS = json.loads((DOCS / "evidence" / "numbers.json").read_text(encoding="utf-8-sig"))
FIG = {f["id"]: f for f in NUMBERS["figures"]}
wb = load_workbook(DOCS / "Financial sheet.xlsx", data_only=True)
case = wb["Financial case"]
summary = wb["Summary"]
inputs = wb["Inputs"]
if case["B4"].value is None:
    raise SystemExit("The financial sheet has no cached values: run excel_recalc.ps1 first.")


def v(fid, key=None):
    val = FIG[fid]["value"]
    return val[key] if key else val


def eur(x):
    return f"€{round(x):,}"


def k(x):
    return f"€{x / 1000:.1f}k"


# One claim, one number (the user's decision, 31 Oct): the claim is the sheet's model column (B); where it comes from is
# the sheet's 'Benefit by source'. The page shows no score (the user, 1 Nov: the reader can do the math).
claim = {k: case[f"B{r}"].value for k, r in (("month", 4), ("net", 10), ("roi", 11), ("payback", 13))}
money = "\n".join([
    "| Year 1 | The claim |",
    "|---|---|",
    f"| Extra gross profit a month at full scope | {eur(claim['month'])} |",
    f"| Net benefit (11 live months − all costs) | {eur(claim['net'])} |",
    f"| ROI | {claim['roi']:.0%} |",
    f"| Payback (months) | {claim['payback']:.1f} |",
])
src_ws = wb["Benefit by source"]


def src_row(r):
    return f"| {src_ws[f'A{r}'].value} | {eur(src_ws[f'B{r}'].value)} | {src_ws[f'C{r}'].value} ({src_ws[f'E{r}'].value}) |"


sources = "\n".join(["| Where the claim's benefit comes from | € a month | Label |", "|---|---|---|"]
                    + [src_row(r) for r in range(4, 8)]
                    + [f"| **The claim, at full scope** | **{eur(src_ws['B11'].value)}** | |",
                       "| Hours saved | not counted | hours, never euros |"])
total_cost = case["B9"].value
one_time = case["B7"].value
running = case["B8"].value
months = case["B5"].value
ai_month = inputs["B11"].value
build_hours = inputs["B8"].value
pilot, share = inputs["B16"].value, inputs["B17"].value
band5 = summary["B30"].value
# By its label, not its row: the lever rows above it grow when a lever is added.
holdout = next(r[1].value for r in inputs.iter_rows(min_col=1, max_col=2) if str(r[0].value or "").startswith("Holdout categories"))
per_ty = v("E11")
e27 = v("E27")
b0 = e27["total"]["vsUntouchedWithSalesHistory"]
e28 = v("E28")


def days(isos):
    from datetime import date
    t = [f"{date.fromisoformat(x).day} {date.fromisoformat(x):%b}" for x in isos]
    return ", ".join(t[:-1]) + " and " + t[-1] if len(t) > 1 else t[0]


last_run = max(e28["gjirafaMallKsRegularRuns"] + e28["gjirafa50KsRegularRuns"] + e28["gjirafa50KsDeadStockRuns"])
first_run = min(e28["gjirafaMallKsRegularRuns"] + e28["gjirafa50KsRegularRuns"])
levers = v("E8", "central")
cw = v("E22")
# The problem's numbers: sales under the floor (E30) and the calculator's runs by hand (E28).
floor_all, floor_gj50 = v("E30", "Both"), v("E30", "Gjirafa50 KS")
floor_margin = floor_all["underFloorGp"] / floor_all["underFloorRevenue"]
rest_margin = (floor_all["gp"] - floor_all["underFloorGp"]) / (floor_all["netRevenue"] - floor_all["underFloorRevenue"])
ks_runs = len(e28["gjirafaMallKsRegularRuns"]) + len(e28["gjirafa50KsRegularRuns"])
gj50_dead = len(e28["gjirafa50KsDeadStockRuns"])
assert round(b0 + levers) == round(claim["month"]), "the engine and the agents no longer add up to the sheet's claim"
money = money.replace("Net benefit (11 live months − all costs)", f"Net benefit ({months:g} full-benefit months − all costs)")

page = f"""# Pricing Tool v2: the price engine, with two AI agents built around it

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx). Page 1 is the
whole case; the pages after it are the detail.

**The problem.** Kosovo's prices are set by hand (the team's process, stated by the user). Margin floors exist per price
band, but in Aug–Sep
{floor_all['revenueSharePct']:.0f} % of the revenue sold under its floor and brought {floor_all['gpSharePct']:.0f} % of the
gross profit (E30); prices change on a few manual dates ({ks_runs} regular runs since July, E28); campaigns are planned by
hand with no set goal (stated by the user); dead stock is cleared in one-off batches a person starts (E28).

**What it does.** It turns the declared pricing calculator (v0.5, no AI in it) into a system that prices both Kosovo shops
every day by itself. **Alpha** (AI) sets each band's floor and target per category every week; **Nova** (AI) writes each
campaign's brief with a goal and Alpha picks its plan; an **AI run checker** confirms, escalates or fixes the prices code
flags; a **publisher** writes the shop's NOP discounts itself, with audit and rollback. The AI decides, code enforces: no
model output changes a price without passing limits written in C#.

**The claim: {eur(claim['month'])} a month at full scope, in two parts.**

| Part | € a month | Label |
|---|---|---|
| **(a) The calculator's effect, measured in a test, made recurring by this build.** The calculator was tested by hand on both Kosovo shops: {ks_runs} regular price runs between {days([first_run])} and {days([last_run])}, none since (E28). In Aug–Sep the products it repriced earned this much more gross profit than untouched products of the same shop, category and weeks (E27). It doesn't run on a schedule today; this build runs it every day. | {eur(b0)} | measured in the test (E27); that it runs only with this build: assumed (stated by the user) |
| **(b) What the agents add,** standing on its own: the loss block, cost reaction, daily cadence, campaign planning, raises that reach the shelf (walk-forward backtest on Aug–Sep sales, E8) | {eur(levers)} | estimated |
| **The claim** | **{eur(claim['month'])}** | year 1: net benefit {eur(claim['net'])}, ROI {claim['roi']:.0%}, payback {claim['payback']:.1f} months |

**The biggest assumption:** that the test's effect, measured over two months, holds for a year and at full scale (every product
of both shops, every day), and that demand answers a price change at elasticity −1 (the agents' part).

**Real data.** A read-only copy of the shop's NOP replica: booked sales lines with their cost, products, discounts. Each
engine run prices every product of both Kosovo shops (511,198 GjirafaMall and 129,708 Gjirafa50 SKUs, 1 Nov).

**What is mocked or not finished.** Publishing goes only to a sandbox copy of the shop's tables, never the live shop; the
live shop still gets its prices through a checked file the team pushes. Injected test cases run in a separate test
database, labelled [INJECTED-TEST]. Not finished, among others (the full list follows): the Për ty reproduction test
fails by one point (E11), the campaign
estimates aren't calibrated yet (E10), the money ledger has measured nothing yet, and the laptop's start-up task is
disabled.

<!-- page break -->

## The problem: four gaps in how we price

1. **No margin control.** Cross-dock products (bought from the supplier after the customer orders) can sell at a loss:
   {eur(v('E14'))} a month of gross profit lost on lines sold under their booked cost (last 12 months, E14, measured). Then
   come the thin margins. Margin floors exist for them, set by hand per price band, but prices often sit under them: in
   Aug–Sep **{floor_all['revenueSharePct']:.0f} % of Kosovo's revenue sold under its floor and brought only
   {floor_all['gpSharePct']:.0f} % of the gross profit**, a {floor_margin * 100:.0f} % margin against {rest_margin * 100:.0f} % on the rest
   (Gjirafa50 alone: {floor_gj50['revenueSharePct']:.0f} % of its revenue, {floor_gj50['gpSharePct']:.0f} % of its profit; E30, measured).
2. **Prices stand still.** Marketing sets the prices of general products for a whole month, the change takes a few days
   to go live, and in between there is little or no dynamic control, so a cost rise waits for the next list (the team's
   process, as stated by the user). The calculator ran by hand too: {ks_runs} regular price runs in Kosovo since July, and
   Gjirafa50 once went {e28['gjirafa50KsLongestGapDays']} days between them (E28, measured).
3. **Campaigns are slow and have no target.** Each one is planned by hand and takes a lot of time and effort, and it
   starts with no set goal for what it should earn or what margin it may give up (stated by the user).
4. **No dead-stock strategy.** Old stock is cleared in one-off batches a person starts: {gj50_dead} on Gjirafa50 since July,
   none on a schedule (E28, measured).

## The answer: one price engine, run by AI agents

**The engine, run by its agents, promises {eur(claim['month'])} more gross profit every month.** The engine is the pricing calculator (v0.5,
declared, tested by hand since July): it prices every product of both Kosovo shops against a margin floor and a target
per price band, charm-rounded, never above the anchor, with dead stock on its own lane, marked down by age. In its
Aug–Sep test on the real shop, the products it repriced earned **{eur(b0)} a month** more gross profit than untouched
products (E27, measured). Run every day by the agents, with what they add (the loss block, cost reaction, daily cadence,
campaign planning, raises that reach the shelf), it earns **{eur(levers)} a month** more (estimated, E8).

## The agents and their crew

All built this weekend (everything after the labelled import commit).

| | In a few words | What it does | Fixes |
|---|---|---|---|
| **Alpha**, the pricing agent (AI) | *Floors that move* | Sets each band's floor and target per main category, every week, within limits in code | 1, 2 |
| **Nova**, the campaign agent (AI) | *Every campaign gets a goal* | Keeps the calendar and writes each brief with a goal; Alpha picks one of four plans code computes, charm-rounded, never under cost or a 3 % margin on any SKU | 3 |
| **Run checker** (AI) | *A second look* | Confirms, escalates or fixes the flagged prices of every run; it can't lift a rule's block | 1 |
| **Cost watcher** (code) | *A 15-minute reflex* | On 31 Oct it read the morning's {cw['distinctMoves']} real cost moves within {cw['minutesAfterRefresh']['first']:g}–{cw['minutesAfterRefresh']['last']:g} minutes; no rise was raised: {cw['risesStillAboveTheFloor']} of {cw['rises']} rises left the price above its floor, the other {cw['heldTeamDeal'] + cw['heldCostAboveShelf']} went to a person (E22) | 1, 2 |
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
reproduction test fails by one point (expected margin {per_ty['expectedMarginPct']} % within bounds, but
{per_ty['keptPctOurBase']} % of normal margin kept against {per_ty['requiredKeptPct']} %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email isn't built and the start-up task is disabled; setting a price on the
Proposals tab doesn't publish it; the money ledger has measured nothing yet.

## The money

**The claim (12 months): {eur(claim['month'])} a month at full scope** (year 1: net benefit {eur(claim['net'])}, ROI
{claim['roi']:.0%}, payback {claim['payback']:.1f} months).

**Where it comes from.** *The price engine, measured:* in the calculator's Aug–Sep test on the real shop, the products
it repriced earned {eur(b0)} a month more gross profit than untouched products in the same shop and category in the same
weeks (E27; booked sales lines, booked COGS; the evidence file reruns it, and a placebo before the calculator's first
approval shows no such lift, E29). *The AI agents, estimated:* {eur(levers)} a month more from the controlled pricing logic
the agents run: the loss block, cost reaction, daily cadence, campaign planning and raises that reach the shelf
(walk-forward backtest, E8). Together {eur(b0 + levers)} a month. The engine was a test, run by hand; this build makes it
permanent and automatic, and the agents learn from the measured outcome of each change. *It runs by itself (on the
sandbox during the hackathon):* Run week on Gjirafa50 KS on Monday's fresh data, started ten minutes before the demo and
shown in it: the proposed prices, the check, the publish to the local sandbox with its rollback, the read-back, every
number traced to a query. The past months are not claimed; Aug–Sep is the evidence for the rate.

**The test, and why it counts.** The calculator (v0.5) ran only as a test, by hand, with no schedule (E28, measured from
the tool's approvals and the pushed discount sets): GjirafaMall KS on {days(e28['gjirafaMallKsRegularRuns'])};
Gjirafa50 KS on {days(e28['gjirafa50KsRegularRuns'])}, with dead-stock runs on {days(e28['gjirafa50KsDeadStockRuns'])}; North
Macedonia and Albania had full pushes in July (Gjirafa50 AL), September (GjirafaMall AL) and on 21 Oct (all four). The
last run was {days([last_run])}; nothing has run since. Without this build it doesn't go live (assumed: stated by the
user, who ran the tests). The rate above was measured in Aug–Sep 2026 on the runs live then: GjirafaMall KS's of 21 Jul,
6 Aug and 3 Sep (with smaller floor and dead-stock sets) and Gjirafa50 KS's of 21 Jul with its dead-stock runs (E27).

**Coverage.** Each engine run prices every product of both Kosovo shops: GjirafaMall KS, 511,198 SKUs, and Gjirafa50
KS, 129,708 SKUs (runs #1111 and #1110, 1 Nov 2026, measured). Nothing goes to the live shop yet: the runs only
compute, and publishing writes to the sandbox. Live, it would price the same products, every category included.
North Macedonia and Albania are built in but not in the claim. **What makes it live-ready** is what this weekend built:
the two agents, the limits in code, the run checker, the cost watcher, the outcome tracker and the publisher with audit
and rollback; going live is configuration and a write login (`GO-LIVE.md`).

{sources}

The levers come mostly from automation, not from the model's own floor choices (lever 2, {eur(v('E2', 'central'))} a
month, E2). GjirafaMall lines are Gjirafa's own vendors only, at booked cost: no marketplace commission applies; cancelled
orders are excluded and returns after delivery are not netted out. The year ramps up: built in month 1, a
{pilot:g}-month pilot at {share:.0%} of the benefit, then full scope ({months:g} full-benefit months); payback comes from that ramp
(the pilot's assumptions are not tuned to cross a line). Costs:
{eur(one_time)} one-time (hackathon hours at €50, {build_hours:g} hours to production including code review, the build
sessions' AI usage of ${v('E18', 'sinceKickoffUsd'):,.2f} API-equivalent, E18) and {eur(running)} running (AI
{eur(ai_month)} a month from the measured cost per call, E19; maintenance and review hours), {eur(total_cost)} in all.

{money}

## The risk, and the ask

**Biggest unproven assumption.** That two months (Aug–Sep 2026) stand for a year and hold at full scale (every product of
both shops, every day), and how demand answers a price change:
the levers score changed prices at elasticity −1 (assumed). The publisher writes raises as the regular price, on the
sandbox so far. The pilot's money ledger turns these into measurements, each push against the shop's products it didn't
change.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); every category priced, with the money ledger measuring each push against the
shop's products it didn't change. That makes the go/no-go measured, not estimated.
"""
out = DOCS / "one-pager.md"
out.write_text(page, encoding="utf-8")
print("saved", out, len(page.split()), "words")
