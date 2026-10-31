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
                    + [f"| The holdout categories' share, left out | −{eur(-src_ws['B10'].value)} | estimated |",
                       f"| **The claim, at full scope** | **{eur(src_ws['B11'].value)}** | |",
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
e20 = v("E20")
shop = e20["byShopAugSepAvg"]
e27 = v("E27")
# Every check on the rate, so no reader finds a number we left out.
e29 = v("E29")
real = [r["real"] for r in e29["byRun"].values()]
placebo = [x for r in e29["byRun"].values() for kind, x in r.items() if kind != "real"]


def signed_k(x):
    return ("−" if x < 0 else "+") + k(abs(x))


rate_checks = (f"against untouched products in the same shop and category it holds ({k(e27['total']['vsUntouched'])}) when every "
               f"product counts, it is {k(e27['total']['vsUntouchedWithSalesHistory'])} when only products with a sale before the "
               f"windows count (a first-ever sale inside the windows leaves both groups), and "
               f"{k(e27['total']['vsUntouchedSoldInBothWindows'])} when only products that sold in both "
               f"windows count; September alone gives "
               f"{k(e27['september']['gjirafaMallVsUntouched'])}–{k(e27['september']['gjirafaMallSoldInBothWindows'])} for GjirafaMall. "
               f"The same comparison in windows before the calculator's first approval (a placebo, E29: centred on 15 May and "
               f"15 Jun 2026, the same product groups) gives {signed_k(min(placebo))} to {signed_k(max(placebo))} a month per run, "
               f"against {signed_k(min(real))} to {signed_k(max(real))} in the real windows")
e28 = v("E28")


def days(isos):
    from datetime import date
    ds = [date.fromisoformat(x) for x in isos]
    t = [f"{d.day} {d:%b}" for d in ds]
    return ", ".join(t[:-1]) + " and " + t[-1] if len(t) > 1 else t[0]
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
switched on superseding only those, and held back the margin trim's cuts: 49,214 of 50,969 GjirafaMall and 25,390 of 26,604
Gjirafa50 checked prices are on the sandbox shelf.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports with the holdout left out); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin {per_ty['expectedMarginPct']} % within bounds, but
{per_ty['keptPctOurBase']} % of normal margin kept against {per_ty['requiredKeptPct']} %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email isn't built and the start-up task is disabled; setting a price on the
Proposals tab doesn't publish it; the money ledger has measured nothing yet.

**Money (12 months). The claim: {eur(claim['month'])} a month at full scope, {score:.0f} points** (Evidence and Works are our own ratings; year 1: net benefit
{eur(claim['net'])}, ROI {claim['roi']:.0%}, payback {claim['payback']:.1f} months).

**Two proofs.** *The engine works:* the Aug–Sep test on the real shop, B0 = {eur(e20['mid'])} a month (GjirafaMall
{eur(shop['mallKs'])}, Gjirafa50 {eur(shop['gj50Ks'])}; E20), measured before and after each approval; robustness checks:
E20, E27 (the evidence file reruns both): {rate_checks}. Gjirafa50 KS's figure is mostly its 21 July run carried forward, plus the three
dead-stock runs. *It runs by itself (on the sandbox during the hackathon):* this build. Run week on Gjirafa50 KS on Monday's fresh data, started ten minutes before the demo and shown in it: the
proposed prices, the check, the publish to the local sandbox with its rollback, the read-back, every number traced to a
query.

**The August–October runs were a test** (stated by the user, who ran them): one person, by hand, with no schedule. The build turns the test into a process that runs by itself; without it, the test ends. What is measured
(E28): one person has run it by hand since 21 Jul, with no schedule. GjirafaMall KS ran on
{days(e28['gjirafaMallKsRegularRuns'])}; Gjirafa50 KS's regular prices were set on 21 Jul and not refreshed for
{e28['gjirafa50KsLongestGapDays']} days (dead-stock runs in that gap on {days([d for d in e28['gjirafa50KsDeadStockRuns'] if d < '2026-10-01'])}); on MK/AL, in Aug–Sep only
GjirafaMall AL had a full push. Even when it ran, {e28['unitsAtOldPriceAugSepPct']} % of the units on raised products still
sold at the old price in Aug–Sep (E23; late October's 70–84 % was the month two push sets overlapped). So the build's
value is the stream it makes permanent, plus the levers:

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

**Biggest unproven assumptions.** That the before/after rate (E20 mid) is the calculator's effect and holds once
automated and extended to every layer (robustness checks: E20, E27). That the August–October test ends without the build (stated by the user, who ran it; the
facts are E28). That the prices reach the shelf: levers 1, 3 and 5 earn mostly by raising prices. The publisher now
writes raises as the regular price, but only on the sandbox so far, and a cheaper discount it didn't write still wins
until a person supersedes it (43 % of the checked prices on 31 Oct, almost all the calculator's own earlier pushes); the
levers' gain on lines sold under a team deal ({eur(v('E8', 'underTeamDealCentral'))} a month) is left out of the claim.
That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change: the claim scores changed prices
at elasticity −1 (assumed). The pilot's holdout categories turn these into measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
"""
out = DOCS / "one-pager.md"
out.write_text(page, encoding="utf-8")
print("saved", out, len(page.split()), "words")
