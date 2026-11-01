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


# One claim, one number (the user's decision, 31 Oct): the claim is the sheet's model column (B) and its score; where it
# comes from is the sheet's 'Benefit by source'.
claim = {k: case[f"B{r}"].value for k, r in (("month", 4), ("net", 10), ("roi", 11), ("payback", 13))}
score = summary["B20"].value
money = "\n".join([
    "| Year 1 | The claim |",
    "|---|---|",
    f"| Extra gross profit a month at full scope | {eur(claim['month'])} |",
    f"| Net benefit (11 live months − all costs) | {eur(claim['net'])} |",
    f"| ROI | {claim['roi']:.0%} |",
    f"| Payback (months) | {claim['payback']:.1f} |",
    f"| Track A score (/100; Evidence and Works are our own ratings) | {score:.0f} |",
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
gj50_dead_in_test = [d for d in e28["gjirafa50KsDeadStockRuns"] if d < "2026-10-01"]
levers = v("E8", "central")
e21 = v("E21")
cw = v("E22")


def cause(n):
    return next(c for k, c in e21["augSepByCause"].items() if k.startswith(n + " "))


e21_free = cause("1")["both"] + cause("3")["both"]
money = money.replace("Net benefit (11 live months − all costs)", f"Net benefit ({months:g} full-benefit months − all costs)")

page = f"""# Pricing Tool v2: two AI agents that set prices, plan campaigns and publish discounts

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx).

**The problem.** The pricing calculator (v0.5, declared, running since July 2026) only reads and calculates. A person sets
every floor and target by hand, plans campaigns in spreadsheets, exports prices and creates the discounts in the shop.
So prices reach customers on a few manual dates, mostly in Kosovo (North Macedonia and Albania got a push in some months
only), and a cost rise waits for the next push: cross-dock
lines alone sold {eur(v('E14'))} a month under their booked cost over the last 12 months (E14, measured). In Aug–Sep
{eur(e21['augSepTotal'])} a month of gross profit went under the hand-set floor (E21), but it isn't one leak:
{eur(e21_free)} of it is allowed or not a price (dead stock, vouchers), {eur(cause('2')['both'])} is GjirafaMall SKUs that take
Gjirafa50's price, and the rest sits where the agents' levers work (loss sales, cost reaction, days without a calculator
price).

**What's new (built this weekend, everything after the labelled import commit).** **Alpha**, the pricing agent, sets
each band's floor and target per main category every week. **Nova**, the campaign agent, keeps the calendar, writes
briefs with goals, and Alpha turns each into a plan that code computes (four strategies, charm-rounded, never under
cost or a 3 % margin on any SKU). An AI run checker confirms, escalates or fixes flagged prices, and can't lift a
rule's block; a cost watcher checks costs every 15 minutes (on 31 Oct it read the morning's {cw['distinctMoves']} real cost moves within
{cw['minutesAfterRefresh']['first']:g}–{cw['minutesAfterRefresh']['last']:g} minutes of the refresh, raised none because no rise put a price we set under its floor, and held
{cw['heldTeamDeal'] + cw['heldCostAboveShelf']} for a person: {cw['heldTeamDeal']} under the floor at a cheaper deal, of them {cw['heldTeamDealBy']['teamDeal']} a team deal and the rest the calculator's own earlier push or an app-only deal, {cw['heldCostAboveShelf']} with a floor above the shelf; E22); an outcome tracker is built to measure every applied change and roll
back what hurts (tested; no pushed change has completed its window yet); a publisher writes the shop's NOP discounts itself, with audit and rollback, and **Run week** runs the
whole loop for a layer with one press; a backtest; and an app of four tabs: Run (the current run as live, its expected effect,
Alpha's account), Chat (the agents' activity and only what needs a person), Campaigns (the calendar, with Nova to talk each
campaign through: re-plan, cancel, a whole-shop campaign on both shops) and Proposals. **The AI decides, code enforces:** no model output changes a
price, band or discount without passing limits written in C#; campaign terms a person gives in the chat (cancellations too) are the model's reading of
the message, bounded by code but not checked against the message itself. During the hackathon the publisher writes only to a
sandbox copy of the shop's price tables; going live is configuration, a DBA's grant, the shop's answers on its cache and
`OldPrice`, and the owner's call on superseding (`GO-LIVE.md`), never yet run
against the live shop. A discount can only lower a price, so a raise is written as the store's regular price: on 31 Oct's
Run weeks 57 % of the two KS layers' checked prices reached the sandbox shelf (909 of them raises); the rest were held by cheaper discounts,
96.5 % of them the calculator's own October pushes the team imported, not team deals (1.5 % of the checked prices). The user then
switched on superseding only those, and on 31 Oct held back the margin trim's cuts (49,214 of 50,969 GjirafaMall and 25,390 of
26,604 Gjirafa50 checked prices then on the sandbox shelf). On 1 Nov the user let the cuts go out again: that day's Run weeks put
133,957 of 135,255 GjirafaMall and 32,225 of 33,219 Gjirafa50 checked prices on the sandbox shelf, all read back as written.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin {per_ty['expectedMarginPct']} % within bounds, but
{per_ty['keptPctOurBase']} % of normal margin kept against {per_ty['requiredKeptPct']} %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email isn't built and the start-up task is disabled; setting a price on the
Proposals tab doesn't publish it; the money ledger has measured nothing yet.

**Money (12 months). The claim: {eur(claim['month'])} a month at full scope, {score:.0f} points** (Evidence and Works are our own ratings; year 1: net benefit
{eur(claim['net'])}, ROI {claim['roi']:.0%}, payback {claim['payback']:.1f} months).

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
Gjirafa50 KS on {days(e28['gjirafa50KsRegularRuns'])}, with dead-stock runs on {days(gj50_dead_in_test)}; North
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

**Biggest unproven assumption.** That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change:
the levers score changed prices at elasticity −1 (assumed). The publisher writes raises as the regular price, on the
sandbox so far. The pilot's money ledger turns these into measurements, each push against the shop's products it didn't
change.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); every category priced, with the money ledger measuring each push against the
shop's products it didn't change. That makes the go/no-go measured, not estimated.
"""
out = DOCS / "one-pager.md"
out.write_text(page, encoding="utf-8")
print("saved", out, len(page.split()), "words")
