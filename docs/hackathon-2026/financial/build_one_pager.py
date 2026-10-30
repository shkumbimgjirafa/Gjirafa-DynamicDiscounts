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
    "| Extra gross profit a month | " + " | ".join(eur(case[f"{c}4"].value) for c in cols.values()) + " |",
    "| Net benefit (11 live months − all costs) | " + " | ".join(eur(case[f"{c}10"].value) for c in cols.values()) + " |",
    "| ROI | " + " | ".join(f"{case[f'{c}11'].value:.0%}" for c in cols.values()) + " |",
    "| Payback (months) | " + " | ".join(f"{case[f'{c}13'].value:.1f}" for c in cols.values()) + " |",
    "| Track A score (/100; Evidence and Works are our own ratings) | " + " | ".join(f"{summary[f'{c}20'].value:.0f}" for c in cols.values()) + " |",
])
total_cost = case["B9"].value
one_time = case["B7"].value
running = case["B8"].value
ai_month = inputs["B10"].value
band5 = summary["B30"].value
per_ty = v("E11")
e20 = v("E20")

page = f"""# Pricing Tool v2: two AI agents that set prices, plan campaigns and publish discounts

Gjirafa AI Hackathon 2026, Track A (Money). Team: Shkumbim (solo, building with Claude Code). Figures carry their ID in
[`evidence/numbers.json`](evidence/numbers.json); the money is the [financial sheet](Financial%20sheet.xlsx).

**The problem.** The pricing calculator (v0.5, declared, running since July 2026) only reads and calculates. A person sets
every floor and target by hand, plans campaigns in spreadsheets, exports prices and creates the discounts in the shop.
So prices reach customers on a few manual dates, only in Kosovo, and a cost rise waits for the next push: cross-dock
lines alone sold {eur(v('E14'))} a month under their booked cost over the last 12 months (E14, measured).

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
{per_ty['expectedMarginPct']} % within bounds, but {per_ty['keptPctOurBase']} % of normal margin kept against
{per_ty['requiredKeptPct']} %; E11); the campaign estimates aren't calibrated yet (E10); the Monday email and the start-up task aren't live.

**Money (12 months, both framings kept apart).** Framing A counts only what this build adds, backtested walk-forward on
booked sales lines: {eur(v('E8', 'low'))} / {eur(v('E8', 'central'))} / {eur(v('E8', 'high'))} a month (E8,
estimated), short of the {k(band5)} a month that the top net-benefit band needs with these costs. Framing B adds the rollout of the
calculator's test phase: {eur(e20['low'])} a month measured with a same-weeks control (a floor: one run on one layer),
{eur(e20['mid'])} before/after and {eur(e20['high'])} season-adjusted, neither controlled (E20). B claims no past euro:
the registration said the calculator's measured Aug–Sep result is not claimed; B counts only the next 12 months of the
automated system, using that result as the rate. Costs: {eur(one_time)} one-time (hackathon hours at €50, 120 hours
to production, the build sessions' AI usage of ${v('E18', 'sinceKickoffUsd'):,.2f} API-equivalent, E18) and
{eur(running)} running (AI {eur(ai_month)} a month from the measured cost per call, E19; maintenance and review hours),
{eur(total_cost)} in all. Which framing counts is the organisers' call.

{money}

**Biggest unproven assumption.** How demand answers a price change: the backtest scores changed prices at elasticity
−2 / −1 / −0.5 (low / mid / high), and framing B assumes the test phase's rate holds once automated and extended, while its mid and high
cases are before/after, not controlled. The pilot's holdout categories turn both into measurements.

**The pilot ask.** Six to eight weeks in Kosovo: Black Friday planned by the two agents, with a campaign holdout; the
publisher switched from the sandbox to the shop for a few categories in autonomous mode (a least-privilege write login,
one canary category first, `GO-LIVE.md`); holdout categories kept on hand-set floors as the control, with the money
ledger measuring the difference. That makes the go/no-go measured, not estimated.
"""
out = DOCS / "one-pager.md"
out.write_text(page, encoding="utf-8")
print("saved", out, len(page.split()), "words")
