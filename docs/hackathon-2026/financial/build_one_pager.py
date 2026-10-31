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


cols = {"A · Low": "B", "A · Mid": "C", "A · High": "D", "B · Low": "E", "B · Mid": "F", "B · High": "G"}
money = "\n".join([
    "| Year 1 | " + " | ".join(cols) + " |",
    "|---|" + "---|" * len(cols),
    "| Extra gross profit a month at full scope | " + " | ".join(eur(case[f"{c}4"].value) for c in cols.values()) + " |",
    "| Net benefit (11 live months − all costs) | " + " | ".join(eur(case[f"{c}10"].value) for c in cols.values()) + " |",
    "| ROI | " + " | ".join(f"{case[f'{c}11'].value:.0%}" for c in cols.values()) + " |",
    "| Payback (months) | " + " | ".join(f"{case[f'{c}13'].value:.1f}" for c in cols.values()) + " |",
    "| Track A score (/100; Evidence and Works are our own ratings) | " + " | ".join(f"{summary[f'{c}20'].value:.0f}" for c in cols.values()) + " |",
])
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
So prices reach customers on a few manual dates, only in Kosovo, and a cost rise waits for the next push: cross-dock
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
{cw['heldTeamDeal'] + cw['heldCostAboveShelf']} for a person: {cw['heldTeamDeal']} under the floor at a cheaper deal, of them {cw['heldTeamDealBy']['teamDeal']} a team deal and the rest the calculator's own earlier push or an app-only deal, {cw['heldCostAboveShelf']} with a floor above the shelf; E22); an outcome tracker measures every applied change and rolls
back what hurts; a publisher writes the shop's NOP discounts itself, with audit and rollback, and **Run week** runs the
whole loop for a layer with one press; a backtest; a chat. **The AI decides, code enforces:** no model output changes a
price, band or discount without passing limits written in C#. During the hackathon the publisher writes only to a
sandbox copy of the shop's price tables; going live is configuration and a DBA's grant (`GO-LIVE.md`), never yet run
against the live shop. A discount can only lower a price, so a raise is written as the store's regular price: on 31 Oct's
Run weeks 57 % of the two KS layers' checked prices reached the sandbox shelf (909 of them raises); the rest were held by cheaper discounts,
96.5 % of them the calculator's own October pushes the team imported, not team deals (1.5 % of the checked prices). Superseding only those, 98.3 % reached it.

**What runs, on real data.** Daily cycles on both Kosovo layers (30 Oct: engine runs #1100 and #1101, 129,675 and 511,093
SKUs; run checks; checked exports with the holdout left out); Alpha's weekly pass #2 changed 8 band × category rules,
each by one point, within its limits; Nova planned Black Friday 2026 against the goal a person set, and the plan was
held for that person because the arithmetic falls short of it. **Not finished, and shown as such:** the Për ty
reproduction test fails by one point (expected margin {per_ty['expectedMarginPct']} % within bounds, but
{per_ty['keptPctOurBase']} % of normal margin kept against {per_ty['requiredKeptPct']} %; E11); the campaign estimates
aren't calibrated yet (E10); the Monday email and the start-up task aren't live; the money ledger has measured nothing yet.

**Money (12 months, both framings kept apart).** Framing A counts only what this build adds, backtested walk-forward on
booked sales lines: {eur(v('E8', 'low'))} / {eur(v('E8', 'central'))} / {eur(v('E8', 'high'))} a month at full scope (E8,
estimated, every lever on the calculator's era, Aug–Sep 2026), short of the {k(band5)} a month that the top
net-benefit band needs with these costs. Most of it comes from automation (the loss block, cost reaction, daily checked
prices, MK/AL coverage, raises that reach the shelf: {eur(v('E24', 'central'))}, E24), not from the model's own floor choices, which the backtest scores at {eur(v('E2', 'central'))}
a month (E2); the holdout categories, about {holdout:.0%} of the changed prices, are left out of the benefit. Framing B adds the rollout of the calculator's test phase: {eur(e20['low'])} a month
measured with a same-weeks control (one controlled run per layer), {eur(e20['mid'])} before/after and {eur(e20['high'])}
season-adjusted, neither controlled (E20; the low and mid cases recomputed from this repo's data); it is the non-AI calculator's effect, which the registration did not claim as
past benefit, and B counts only the next 12 months of the automated system. The year ramps up: built in month 1, a
{pilot:g}-month pilot at {share:.0%} of the benefit, then full scope ({months:g} full-benefit months). Costs:
{eur(one_time)} one-time (hackathon hours at €50, {build_hours:g} hours to production including code review, the build
sessions' AI usage of ${v('E18', 'sinceKickoffUsd'):,.2f} API-equivalent, E18) and {eur(running)} running (AI
{eur(ai_month)} a month from the measured cost per call, E19; maintenance and review hours), {eur(total_cost)} in all.
Which framing counts is the organisers' call.

{money}

**Biggest unproven assumptions.** That the prices reach the shelf: levers 1, 3 and 5 earn mostly by raising prices. The
publisher now writes raises as the regular price, but only on the sandbox so far, and a cheaper discount it didn't write still
wins until a person supersedes it (43 % of the checked prices on 31 Oct, almost all the calculator's own earlier pushes).
In today's process 17–20 % of the units on SKUs an approval raised still sold at the old price in Aug–Sep, 70–84 % in late
October (E23); the levers' gain on lines sold under a team deal ({eur(v('E8', 'underTeamDealCentral'))} a month) counts only in the high case. That two months (Aug–Sep 2026) stand for a year, and how demand answers a price change:
the backtest scores changed prices at elasticity −2 / −1 / −0.5 (low / mid / high). Framing B also assumes the test
phase's rate holds once automated and extended to every layer. The pilot's holdout categories turn these into
measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
"""
out = DOCS / "one-pager.md"
out.write_text(page, encoding="utf-8")
print("saved", out, len(page.split()), "words")
