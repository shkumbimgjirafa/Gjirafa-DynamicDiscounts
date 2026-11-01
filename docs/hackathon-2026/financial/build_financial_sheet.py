"""Builds docs/hackathon-2026/Financial sheet.xlsx (Track A money formula) from evidence/numbers.json.

Every input that has a figure in numbers.json is typed from it and carries its ID; every other input is labelled assumed
or judgment with its reason. All other cells are live formulas. After building, recalculate in Excel so the file carries
cached values (the one-pager reads them):

    python -I docs/hackathon-2026/financial/build_financial_sheet.py
    powershell -ExecutionPolicy Bypass -File docs/hackathon-2026/financial/excel_recalc.ps1 -Path "<repo>/docs/hackathon-2026/Financial sheet.xlsx"

First drafted before the hackathon (2026-10-29) with placeholders; the placeholders are gone.
"""
import json
from datetime import datetime
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side

HERE = Path(__file__).resolve().parent
DOCS = HERE.parent
OUT = DOCS / "Financial sheet.xlsx"
NUMBERS = json.loads((DOCS / "evidence" / "numbers.json").read_text(encoding="utf-8-sig"))
FIG = {f["id"]: f for f in NUMBERS["figures"]}


def v(fid, key=None):
    val = FIG[fid]["value"]
    return val[key] if key else val


def label(fid, key=None):
    lab = FIG[fid]["label"]
    return lab[key] if isinstance(lab, dict) else lab


ARIAL = "Arial"
F = lambda **k: Font(name=ARIAL, size=10, **k)
BLUE = F(color="0000FF")
BLACK = F()
GREEN = F(color="008000")
BOLD = F(bold=True)
TITLE = Font(name=ARIAL, size=14, bold=True)
NOTE = Font(name=ARIAL, size=9, italic=True, color="595959")
YELLOW = PatternFill("solid", fgColor="FFFF00")
HEAD = PatternFill("solid", fgColor="D9E1F2")
TOTAL = PatternFill("solid", fgColor="E2EFDA")
thin = Side(style="thin", color="BFBFBF")
BOX = Border(top=thin, bottom=thin, left=thin, right=thin)

EUR = '€#,##0;(€#,##0);"-"'
USD = '$#,##0.00;($#,##0.00);"-"'
USD4 = '$0.0000'
PCT = '0.0%;(0.0%);"-"'
NUM1 = '0.0;(0.0);"-"'
NUM0 = '#,##0;(#,##0);"-"'
NUM2 = '0.00'

wb = Workbook()


def put(ws, ref, value, font=BLACK, fmt=None, fill=None, bold=False, wrap=False):
    c = ws[ref]
    c.value = value
    c.font = Font(name=ARIAL, size=font.size, bold=bold or font.bold, italic=font.italic, color=font.color)
    if fmt:
        c.number_format = fmt
    if fill:
        c.fill = fill
    if wrap:
        c.alignment = Alignment(wrap_text=True, vertical="top")
    return c


def header(ws, row, labels, start_col=1):
    for i, lab in enumerate(labels):
        c = ws.cell(row=row, column=start_col + i, value=lab)
        c.font = BOLD
        c.fill = HEAD
        c.border = BOX
        c.alignment = Alignment(wrap_text=True, vertical="center")


# Rows of the Inputs sheet, by name: every formula elsewhere refers to them through this map.
R = dict(rate=5, hours=6, person=7, build=8, training=9, setup=10, ai=11, servers=12, maint=13, review=14,
         first=15, pilot=16, share=17, months=18, buildAi=19, fx=20,
         levHead=22, base=23, catch=24, lev1=25, lev8=31, levSum=33, levCheck=34, holdout=35,
         b0Head=36, b0Low=37, b0Mid=38, b0High=39, b0UsedHead=41, b0Used=42,
         withoutBuild=43, judgeHead=44, evB=46, works=47,
         aiHead=49, aiFirst=50, aiLast=54, aiSum=55)
I = {k: f"Inputs!$B${r}" for k, r in R.items()}

built = datetime.now().strftime("%Y-%m-%d %H:%M")
snapshot = FIG["E1"]["source"]
# One claim, one number (the user's decision, 31 Oct): the model has one column, the claim. B0's robustness checks
# (E20 low, E27) stay on Inputs and the Evidence sheet as measured figures, labelled.
cols = ["B"]
scen = ["The claim"]
CLAIM = 'B'

# ---------------------------------------------------------------- Summary
ws = wb.active
ws.title = "Summary"
put(ws, "A1", "Track A score: AI pricing and campaign agents", TITLE)
put(ws, "A2", f"Built {built} by docs/hackathon-2026/financial/build_financial_sheet.py from evidence/numbers.json "
              f"(backtest: {snapshot}). Every input on 'Inputs' carries its numbers.json ID or its label (assumed / "
              "judgment); every cell below is a formula.", NOTE, wrap=True)
ws.merge_cells("A2:G2")
ws.row_dimensions[2].height = 40
put(ws, "A3", "The claim: the automated system's next 12 months: the price engine's measured gain (B0, E27: repriced vs "
              "untouched products in the calculator's Aug–Sep test) plus what the AI agents' controlled pricing logic adds (the "
              "levers, central case), every category priced (no holdout categories). The past months are not claimed; Aug–Sep is the "
              "evidence for the rate. Payback is computed from the ramp on Inputs rows 15–18 (built in month 1, then the pilot).",
    NOTE, wrap=True)
put(ws, "A4", '="The claim: "&TEXT(B6,"€#,##0")&" a month at full scope; year 1: net benefit "&TEXT(B7,"€#,##0")'
              '&", ROI "&TEXT(B10,"0%")&", payback "&TEXT(B13,"0.0")&" months, "&TEXT(B20,"0")&"/100"', BOLD)
ws.merge_cells("A4:G4")
ws.merge_cells("A3:G3")
ws.row_dimensions[3].height = 52

header(ws, 5, ["Row (weight)", "The claim"])
rows = [
    ("Monthly benefit at full scope (€/month)", f"='Financial case'!{CLAIM}4", EUR, GREEN),
    ("Net benefit, year 1 (€)", f"='Financial case'!{CLAIM}10", EUR, GREEN),
    ("  Net benefit rating (0–5)", "=IF(B7>100000,5,IF(B7>40000,4,IF(B7>15000,3,IF(B7>5000,2,IF(B7>0,1,0)))))", "0", BLACK),
    ("  Net benefit points (40)", "=B8/5*40", NUM0, BLACK),
    ("ROI, year 1", f"='Financial case'!{CLAIM}11", PCT, GREEN),
    ("  ROI rating (0–5)", "=IF(B10>2,5,IF(B10>1,4,IF(B10>0.5,3,IF(B10>0.25,2,IF(B10>0,1,0)))))", "0", BLACK),
    ("  ROI points (10)", "=B11/5*10", NUM0, BLACK),
    ("Payback (months)", f"='Financial case'!{CLAIM}13", NUM1, GREEN),
    ("  Payback rating (0–5)", "=IF(ISNUMBER(B13),IF(B13<=1,5,IF(B13<=3,4,IF(B13<=6,3,IF(B13<=9,2,IF(B13<=12,1,0))))),0)", "0", BLACK),
    ("  Payback points (10)", "=B14/5*10", NUM0, BLACK),
    ("Evidence rating (0–5, judgment)", f"=Inputs!$B${R['evB']}", "0", GREEN),
    ("  Evidence points (20)", "=B16/5*20", NUM0, BLACK),
    ("Works rating (0–5, judgment)", f"=Inputs!$B${R['works']}", "0", GREEN),
    ("  Works points (20)", "=B18/5*20", NUM0, BLACK),
    ("Total (/100)", "=B9+B12+B15+B17+B19", NUM0, BLACK),
    ("Prize eligible (Works ≥ 3)", '=IF(B18>=3,"Yes","No")', None, BLACK),
]
for i, (lab, f, fmt, font) in enumerate(rows):
    r = 6 + i
    put(ws, f"A{r}", lab, BOLD if not lab.startswith("  ") else BLACK)
    put(ws, f"B{r}", f, font, fmt).border = BOX
    if lab.startswith("Total"):
        for c in ("A", "B"):
            ws[f"{c}{r}"].fill = TOTAL
            ws[f"{c}{r}"].font = Font(name=ARIAL, size=10, bold=True)
put(ws, "A23", "Reference: the worked example in the hackathon rules scores 76 / 100.", NOTE)
put(ws, "A24", "Evidence and Works are our own judgment ratings (Inputs rows 46–47), with the reason next to each; a juror "
               "may rate them lower. Where the benefit comes from, line by line: the 'Benefit by source' sheet.", NOTE, wrap=True)
ws.merge_cells("A24:G24")
ws.row_dimensions[24].height = 30

put(ws, "A25", "Monthly benefit at full scope needed for each net-benefit rating, with these costs", BOLD)
header(ws, 26, ["Net-benefit rating", "€ per month needed"])
for i, (lab, thr) in enumerate([("2 (net > €5k)", 5000), ("3 (net > €15k)", 15000),
                                ("4 (net > €40k)", 40000), ("5 (net > €100k)", 100000)]):
    r = 27 + i
    put(ws, f"A{r}", lab)
    put(ws, f"B{r}", f"=({thr}+'Financial case'!${CLAIM}$9)/'Financial case'!${CLAIM}$5", BLACK, EUR).border = BOX
put(ws, "A31", "Costs are the same in every column of the model; benefit ramps up through the pilot (Inputs rows 15–18).", NOTE)

put(ws, "A33", "Rules that apply (hackathon Track A)", BOLD)
for i, t in enumerate([
    "Count margin, not revenue. All benefit here is gross profit on own-retail SKUs (no marketplace seller sales).",
    "Hours saved are capacity, not money, so they are not counted as benefit.",
    "Every person-hour costs €50, including the hackathon hours.",
    "Every input is labelled measured / estimated / assumed / judgment with its source (see Inputs and Evidence).",
]):
    put(ws, f"A{34 + i}", "• " + t, F())
ws.column_dimensions["A"].width = 40
for c in cols:
    ws.column_dimensions[c].width = 13

# ---------------------------------------------------------------- Inputs
wi = wb.create_sheet("Inputs")
put(wi, "A1", "Inputs", TITLE)
put(wi, "A2", "Legend: blue = input typed from numbers.json (ID in the last column) or an assumption · yellow fill = a "
              "key assumption or judgment worth challenging · black = formula.", NOTE, wrap=True)
wi.merge_cells("A2:G2")
wi.row_dimensions[2].height = 30

header(wi, 4, ["Cost input", "Value", "Unit", "Label", "Source / note", "", "ID"])
cost_rows = [
    ("rate", "Hourly rate", 50, "€/h", "rule", "Hackathon rules: every person-hour costs €50", False, ""),
    ("hours", "Hackathon hours (attended blocks)", v("E16", "attendedHours"), "h", label("E16"),
     f"hours.md, the attended rows ({v('E16', 'rows')} rows in all; parallel sessions counted once). The "
     f"{v('E16', 'unattendedBlocks')} unattended blocks are AI cost (row {R['buildAi']}), not person-hours", False, "E16"),
    ("person", "The person's time inside unattended blocks", 1, "h", "assumed",
     "Answering the agents in chat during unattended blocks (the goal, context, reviews); not timed, so assumed", False, ""),
    ("build", "Remaining build to production (one person-week, the user's estimate)", 40, "h", "assumed",
     "Review of the AI-written code, the live shop write-back (a write-enabled login, canary on a few categories), "
     "start-up as a service, the weekly email, MK/AL data loads, Nova hardening, monitoring", True, ""),
    ("training", "Training and sign-off of the limits", 8, "h", "assumed",
     "Hand-over to the pricing and campaign owners; the commercial owner confirms the campaign and safety limits", False, ""),
    ("setup", "Setup fees", 0, "€", "assumed", "No licences; Claude through the team's existing subscription", False, ""),
    ("ai", "AI usage (agents' runtime calls)", f"=D{R['aiSum']}*B{R['fx']}", "€/month", "estimated",
     f"Rows {R['aiFirst']}–{R['aiLast']}: measured cost per call (E19) × assumed calls a month, at the API-equivalent price",
     False, "E19"),
    ("servers", "Servers", 0, "€/month", "assumed",
     "Production runs on the k8s cluster that already runs the calculator (the main repo), with Claude Code's OAuth token "
     "as a secret; the laptop is the hackathon's stand-in", False, ""),
    ("maint", "Maintenance", 8, "h/month", "assumed", "Fixes, data checks, prompt and limit updates for two agents and a shop writer", True, ""),
    ("review", "Exception review", 0.5, "h/week", "assumed",
     "Autonomous mode: a person reviews only held items, pauses and rollbacks (the plan's 15–30 min a week)", False, ""),
    ("first", "First live month (the pilot starts)", 2, "month #", "assumed", "Built and tested in month 1", True, ""),
    ("pilot", "Pilot length at reduced scope", 2, "months", "assumed",
     "The pilot ask: 6–8 weeks on Kosovo, a few categories live first, then the go/no-go", True, ""),
    ("share", "Pilot scope, share of the full benefit", 0.25, "share", "assumed",
     "About one Kosovo layer's few categories; the full benefit counts both Kosovo layers", True, ""),
    ("buildAi", "Build sessions' AI usage, whole build", v("E18", "sinceKickoffUsd"), "USD", label("E18"),
     f"The two Claude Code sessions since kickoff, {v('E18', 'sinceKickoffResponses')} responses; "
     f"${v('E18', 'unattendedBlocksUsd')} of it in the unattended blocks. API-equivalent: the actual spend is the "
     "flat Claude subscription, so this overstates the cash cost", False, "E18"),
    ("fx", "USD to EUR", 1.0, "€ per $", "assumed", "1:1, which overstates the dollar costs in euros slightly (conservative)", False, ""),
]
for key, lab, val, unit, labl, src, yellow, fid in cost_rows:
    r = R[key]
    put(wi, f"A{r}", lab)
    is_formula = isinstance(val, str) and val.startswith("=")
    fmt = (EUR if unit.startswith("€/m") else USD if unit == "USD" else PCT if unit == "share" else NUM2 if key == "fx"
           else NUM1 if isinstance(val, float) else NUM0)
    put(wi, f"B{r}", val, BLACK if is_formula else BLUE, fmt, YELLOW if yellow else None).border = BOX
    put(wi, f"C{r}", unit)
    put(wi, f"D{r}", labl)
    put(wi, f"E{r}", src, NOTE)
    put(wi, f"G{r}", fid, BOLD)
put(wi, f"A{R['months']}", "Months of full benefit in year 1")
put(wi, f"B{R['months']}", f"=(13-B{R['first']}-B{R['pilot']})+B{R['pilot']}*B{R['share']}", BLACK, NUM1).border = BOX
put(wi, f"C{R['months']}", "months")
put(wi, f"D{R['months']}", "formula")
put(wi, f"E{R['months']}", "Full months after the pilot, plus the pilot months at the pilot's share", NOTE)

header(wi, R["levHead"], ["Benefit lever (€/month at full scope)", "Low", "Mid", "High", "Label", "Method / source", "ID"])
put(wi, f"A{R['base']}", "Cross-dock lines sold under booked cost (baseline)")
for col in "BCD":
    put(wi, f"{col}{R['base']}", v("E14"), BLUE, EUR).border = BOX
put(wi, f"E{R['base']}", label("E14"))
put(wi, f"F{R['base']}", FIG["E14"]["method"], NOTE)
put(wi, f"G{R['base']}", "E14", BOLD)
put(wi, f"A{R['catch']}", "Share of it the loss-sale block catches")
for col in "BCD":
    put(wi, f"{col}{R['catch']}", f"=IF({col}{R['base']}>0,{col}{R['lev1']}/{col}{R['base']},0)", BLACK, PCT).border = BOX
put(wi, f"E{R['catch']}", "formula")
put(wi, f"F{R['catch']}", "Lever 1 ÷ baseline: many losses come with a cost that jumped at that sale, which a known-cost rule can't see. "
                          "Lever 1 assumes a blocked loss sale costs nothing else (no lost basket)", NOTE)
cases = {"B": "low", "C": "central", "D": "high"}
lev = [("E1", "1. Loss-sale block"), ("E2", "2. Per-category floors"), ("E3", "3. Cost reaction"),
       ("E5", "5. Daily cadence"), ("E6", "6. Learning from past changes (as built)"), ("E7", "7. Campaign planning (campaign + pricing agents)"),
       ("E24", "8. Raises reach the shelf (the publisher)")]
LEVROW = {fid: R["lev1"] + i for i, (fid, _) in enumerate(lev)}       # lever 4 (MK/AL coverage) is not in the claim
for i, (fid, lab) in enumerate(lev):
    r = R["lev1"] + i
    put(wi, f"A{r}", lab)
    for col, case in cases.items():
        put(wi, f"{col}{r}", v(fid, case), BLUE, EUR).border = BOX
    put(wi, f"E{r}", label(fid))
    put(wi, f"F{r}", FIG[fid]["method"], NOTE)
    put(wi, f"G{r}", fid, BOLD)
put(wi, f"A{R['levSum']}", "The levers this build adds (levers 1–3 and 5–8)", BOLD)
for col in "BCD":
    c = put(wi, f"{col}{R['levSum']}", f"=SUM({col}{R['lev1']}:{col}{R['lev8']})", BLACK, EUR)
    c.border = BOX
    c.fill = TOTAL
    c.font = Font(name=ARIAL, size=10, bold=True)
put(wi, f"E{R['levSum']}", "formula")
put(wi, f"F{R['levSum']}", "Low = elasticity −2, Mid = −1, High = −0.5 (assumed); each sales line counted by one lever; every lever "
                           "on the calculator's era, Aug–Sep 2026 (lever 3's low case September only: August was one cost event); "
                           f"the raise levers' gain on lines sold under a team deal (€{v('E8', 'underTeamDealCentral'):,.0f} a month at Mid) "
                           "counts in High only: v2 doesn't raise a price a team deal holds; lever 8's High is October's pace (E23)", NOTE)
put(wi, f"A{R['levCheck']}", "Check: the same total in numbers.json")
for col, case in cases.items():
    put(wi, f"{col}{R['levCheck']}", v("E8", case), BLUE, EUR).border = BOX
put(wi, f"E{R['levCheck']}", f'=IF(AND(B{R["levSum"]}=B{R["levCheck"]},C{R["levSum"]}=C{R["levCheck"]},D{R["levSum"]}=D{R["levCheck"]}),"matches","DIFFERS")', BLACK)
put(wi, f"G{R['levCheck']}", "E8", BOLD)
put(wi, f"A{R['holdout']}", "Holdout categories: share left out (none)")
for col in "BCD":
    put(wi, f"{col}{R['holdout']}", 0, BLUE, PCT).border = BOX
put(wi, f"E{R['holdout']}", "the user's decision")
put(wi, f"F{R['holdout']}", "No holdout categories (the user's decision, 1 Nov): the agents price every category, so no part of a "
                            "lever is left out. The money ledger measures each push against the shop's products it didn't change", NOTE)

header(wi, R["b0Head"], ["B0, the price engine's measured gain", "Value", "Unit", "Label", "Source / note", "", "ID"])
put(wi, f"A{R['b0Mid']}", "The calculator's Aug–Sep test: repriced products vs untouched products in the same shop, category and weeks")
put(wi, f"B{R['b0Mid']}", v("E27", "total")["vsUntouchedWithSalesHistory"], BLUE, EUR).border = BOX
put(wi, f"C{R['b0Mid']}", "€/month")
put(wi, f"D{R['b0Mid']}", "measured")
put(wi, f"E{R['b0Mid']}", "Booked sales lines and booked COGS, products with a sales history, campaign days out; a placebo before the "
                          "calculator's first approval shows no such lift (E29). evidence/e20-aug-sep-comparison.sql", NOTE)
put(wi, f"G{R['b0Mid']}", "E27", BOLD)
header(wi, R["b0UsedHead"], ["B0 used in the claim", "", "€/month", "", "Label", ""])
put(wi, f"A{R['b0Used']}", "B0 (€/month)", BOLD)
c = put(wi, f"C{R['b0Used']}", f"=B{R['b0Mid']}", BLACK, EUR)
c.border = BOX
c.fill = TOTAL
put(wi, f"E{R['b0Used']}", "formula")
put(wi, f"A{R['withoutBuild']}", "The engine ran only as a test, by hand (last run 21 Oct, E28); without the build it doesn't go live", BOLD)
put(wi, f"B{R['withoutBuild']}", "yes", BLUE, None, YELLOW).border = BOX
put(wi, f"D{R['withoutBuild']}", "assumed (stated by the user, who ran the tests)")

header(wi, R["judgeHead"], ["Judgment ratings (one per row; they don't depend on the money case)", "Rating", "", "", "Label", "Why"])
judgments = [
    ("evB", "Evidence rating, the claim (0–5)", 3,
     "B0 was measured on the real shop with real sales (the low case with a same-weeks control on each KS layer), before "
     "the hackathon; this build recomputes the low and mid cases from its own data, but they are one run per layer, and "
     "the mid and high cases are before/after, not controlled"),
    ("works", "Works rating (0–5)", 4,
     "The daily cycle ran end to end on real data (engine runs, run checks, checked exports, sandbox publishes) and the "
     "failure cases in failure-cases.md pass; not 5: the Për ty reproduction test fails by one point, the money ledger "
     "has measured nothing yet, the weekly email isn't built"),
]
for key, lab, val, why in judgments:
    r = R[key]
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", val, BLUE, "0", YELLOW).border = BOX
    put(wi, f"E{r}", "judgment")
    put(wi, f"F{r}", why, NOTE)

header(wi, R["aiHead"], ["AI running cost (agents' calls)", "USD per call", "Calls a month", "USD a month", "Label", "Volume assumption", "ID"])
e19 = v("E19")
ai_rows = [
    ("Run check (Sonnet 5.5)", "runCheck", 183, "one per engine run: 6 layers × 30.5 days"),
    ("Alpha, weekly floors and targets (Opus 5.5)", "alphaWeeklyPass", 26, "one per layer a week: 6 × 4.33"),
    ("Nova, weekly campaign briefs (Opus 5.5)", "novaWeeklyPass", 26, "one per layer a week: 6 × 4.33"),
    ("Campaign planning round (Alpha + Nova)", "planningCall", 12, "about 3 campaigns a month × 2 rounds × 2 calls"),
    ("Chat (Nova or Alpha)", "chat", 43, "about 10 questions a week"),
]
for i, (lab, kind, calls, why) in enumerate(ai_rows):
    r = R["aiFirst"] + i
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", e19[kind]["avgUsd"], BLUE, USD4).border = BOX
    put(wi, f"C{r}", calls, BLUE, NUM0, YELLOW).border = BOX
    put(wi, f"D{r}", f"=B{r}*C{r}", BLACK, USD).border = BOX
    put(wi, f"E{r}", "measured × assumed")
    put(wi, f"F{r}", f"{why}; cost per call averaged over {e19[kind]['calls']} logged calls", NOTE)
    put(wi, f"G{r}", "E19", BOLD)
put(wi, f"A{R['aiSum']}", "AI usage a month (USD)", BOLD)
c = put(wi, f"D{R['aiSum']}", f"=SUM(D{R['aiFirst']}:D{R['aiLast']})", BLACK, USD)
c.border = BOX
c.fill = TOTAL
put(wi, f"E{R['aiSum']}", "formula")
put(wi, f"F{R['aiSum']}", "API-equivalent; through the team's Claude subscription the cash cost is the subscription itself. The "
                          "volume is what the system needs; calls are never made to raise a count", NOTE)

wi.column_dimensions["A"].width = 46
for c in "BCD":
    wi.column_dimensions[c].width = 12
wi.column_dimensions["E"].width = 22
wi.column_dimensions["F"].width = 80
wi.column_dimensions["G"].width = 6

# ---------------------------------------------------------------- Financial case
wf = wb.create_sheet("Financial case")
put(wf, "A1", "Financial case: the Track A money formula", TITLE)
header(wf, 3, ["Line"] + scen + ["How it's calculated"])          # the explanation in column C
s, b = R["levSum"], R["b0Used"]
ho = R["holdout"]
benefit = {"B": f"=(Inputs!C{s}+Inputs!C{b})*(1-Inputs!C{ho})"}     # B0 at E20 mid + the levers' central case
lines = [
    (4, "Monthly benefit at full scope (€/month)", None, EUR, GREEN, "B0 at E20 mid + levers 1–8, central · every category priced (no holdout)"),
    (5, "Months of full benefit in year 1", f"={I['months']}", NUM1, GREEN, "After the build month: the pilot at its share, then full scope"),
    (6, "First-year benefit", "={c}4*{c}5", EUR, BLACK, "Extra margin in the first 12 months"),
    (7, "One-time cost", f"=({I['hours']}+{I['person']}+{I['build']}+{I['training']})*{I['rate']}+{I['setup']}+{I['buildAi']}*{I['fx']}", EUR, GREEN,
     "(Hackathon + the person's time in unattended blocks + remaining build + training hours) × €50 + setup + the build sessions' AI usage"),
    (8, "Running cost (12 months)", f"=12*({I['ai']}+{I['servers']}+{I['maint']}*{I['rate']})+52*{I['review']}*{I['rate']}", EUR, GREEN,
     "12 × (AI + servers + maintenance hours × €50) + 52 × review hours × €50"),
    (9, "Total cost", "={c}7+{c}8", EUR, BLACK, "One-time + running"),
    (10, "Net benefit", "={c}6-{c}9", EUR, BLACK, "First-year benefit − total cost"),
    (11, "ROI", "=IF({c}9>0,{c}10/{c}9,0)", PCT, BLACK, "Net benefit ÷ total cost"),
    (12, "Running cost per month", "={c}8/12", EUR, BLACK, "Used for payback"),
    (13, "Payback (months)", "=Payback!{c}20", NUM1, BLACK, "When cumulative gain covers cumulative spend, from the monthly grid (Payback sheet)"),
]
for r, lab, f, fmt, font, how in lines:
    put(wf, f"A{r}", lab, BOLD if r in (6, 9, 10) else BLACK)
    for c in cols:
        formula = benefit[c] if r == 4 else f.format(c=c)
        put(wf, f"{c}{r}", formula, font, fmt).border = BOX
    put(wf, f"C{r}", how, NOTE)
for c in ["A"] + cols:
    wf[f"{c}10"].fill = TOTAL
wf.column_dimensions["A"].width = 32
for c in cols:
    wf.column_dimensions[c].width = 12
wf.column_dimensions["C"].width = 90

# ---------------------------------------------------------------- Benefit by source
wsb = wb.create_sheet("Benefit by source")
put(wsb, "A1", "Where the claim's benefit comes from (€ a month at full scope)", TITLE)
header(wsb, 3, ["Line", "€ a month", "Label", "Source", "ID"])
src = [
    ("The price engine, measured against untouched products (B0)", f"=Inputs!C{R['b0Used']}",
     "measured", "The calculator's Aug–Sep test, products with a sales history; placebo E29", "E27"),
    ("Recovered leakage: raises that reach the shelf (lever 8)", f"=Inputs!C{LEVROW['E24']}", "estimated", FIG["E24"]["name"], "E24"),
    ("Cadence: prices daily, not on a few manual dates (lever 5)", f"=Inputs!C{LEVROW['E5']}", "estimated", FIG["E5"]["name"], "E5"),
    ("The other levers: loss block, category floors, cost reaction, learning, campaign planning (1, 2, 3, 6, 7)",
     "=" + "+".join(f"Inputs!C{LEVROW[f]}" for f in ("E1", "E2", "E3", "E6", "E7")), "estimated",
     "E1, E2, E3, E6, E7 (walk-forward backtest)", "E1–E3, E6, E7"),
]
for i, (lab, f, lab2, source, fid) in enumerate(src):
    r = 4 + i
    put(wsb, f"A{r}", lab)
    put(wsb, f"B{r}", f, BLACK, EUR).border = BOX
    put(wsb, f"C{r}", lab2)
    put(wsb, f"D{r}", source, NOTE)
    put(wsb, f"E{r}", fid, BOLD)
put(wsb, "A9", "Before the holdout share", BOLD)
put(wsb, "B9", "=SUM(B4:B8)", BLACK, EUR).border = BOX
put(wsb, "A10", "The holdout categories' share, left out (none: every category priced)")
put(wsb, "B10", f"=-B9*Inputs!C{R['holdout']}", BLACK, EUR).border = BOX
put(wsb, "C10", "the user's decision")
put(wsb, "A11", "The claim: monthly benefit at full scope", BOLD)
c = put(wsb, "B11", "=B9+B10", BLACK, EUR)
c.border = BOX
c.fill = TOTAL
put(wsb, "C11", f'=IF(ROUND(B11,0)=ROUND(\'Financial case\'!{CLAIM}4,0),"matches the Financial case","DIFFERS")')
put(wsb, "A12", "Months that count in year 1", BOLD)
put(wsb, "B12", f"={I['months']}", BLACK, NUM1).border = BOX
put(wsb, "D12", "Built in month 1, a two-month pilot at an assumed quarter of the benefit, then full scope (Inputs rows 15–18)", NOTE)
put(wsb, "A13", "Hours saved", BOLD)
put(wsb, "B13", "not counted")
put(wsb, "D13", "Hours, never euros: the time sheet's minutes per task aren't set yet (they wait for the user), and hours saved are not benefit here", NOTE)
put(wsb, "A15", "GjirafaMall lines are Gjirafa's own vendors only, at booked cost: no marketplace commission applies.", NOTE)
put(wsb, "A16", "Cancelled orders are excluded; returns after delivery are not netted out.", NOTE)
wsb.column_dimensions["A"].width = 70
wsb.column_dimensions["B"].width = 14
wsb.column_dimensions["C"].width = 34
wsb.column_dimensions["D"].width = 70

# ---------------------------------------------------------------- Payback
wp = wb.create_sheet("Payback")
put(wp, "A1", "Payback: cumulative net position by month", TITLE)
put(wp, "A2", "Cumulative gain − cumulative spend. One-time cost lands in month 1; benefit starts in the first live month at "
              "the pilot's share, then full scope.", NOTE)
header(wp, 4, ["Month"] + scen + ["Benefit months so far"])
for m in range(1, 13):
    r = 4 + m
    put(wp, f"A{r}", m, BLACK, NUM0)
    factor = f"IF({m}<{I['first']},0,IF({m}<{I['first']}+{I['pilot']},{I['share']},1))"
    put(wp, f"C{r}", f"={factor}" if m == 1 else f"=C{r - 1}+{factor}", BLACK, NUM2).border = BOX
    for c in cols:
        put(wp, f"{c}{r}", f"='Financial case'!{c}$4*$C{r}-('Financial case'!{c}$7+'Financial case'!{c}$12*$A{r})", BLACK, EUR).border = BOX
put(wp, "A18", "First month at or above zero", BOLD)
put(wp, "A19", "Position the month before", BOLD)
put(wp, "A20", "Payback (months, interpolated)", BOLD)
for c in cols:
    put(wp, f"{c}18", f'=IFERROR(MATCH(TRUE,INDEX({c}5:{c}16>=0,0),0),"after 12")', BLACK, NUM0).border = BOX
    put(wp, f"{c}19", f"=IF(ISNUMBER({c}18),IF({c}18=1,-'Financial case'!{c}$7,INDEX({c}5:{c}16,{c}18-1)),\"\")", BLACK, EUR).border = BOX
    put(wp, f"{c}20", f'=IF(ISNUMBER({c}18),{c}18-1+(-{c}19)/(INDEX({c}5:{c}16,{c}18)-{c}19),"after 12")', BLACK, NUM1).border = BOX
wp.column_dimensions["A"].width = 30
for c in cols + ["C"]:
    wp.column_dimensions[c].width = 13

# ---------------------------------------------------------------- Score bands
wb_ = wb.create_sheet("Score bands")
put(wb_, "A1", "Track A score bands (from the hackathon rules)", TITLE)
header(wb_, 3, ["Rating", "Net benefit, year 1", "ROI, year 1", "Payback"])
bands = [
    (0, "€0 or negative", "0% or negative", "More than 12 months / never"),
    (1, "Up to €5,000", "Up to 25%", "9 to 12 months"),
    (2, "€5,000 – €15,000", "25% – 50%", "6 to 9 months"),
    (3, "€15,000 – €40,000", "50% – 100%", "3 to 6 months"),
    (4, "€40,000 – €100,000", "100% – 200%", "1 to 3 months"),
    (5, "Above €100,000", "Above 200%", "1 month or less"),
]
for i, row in enumerate(bands):
    for j, val in enumerate(row):
        put(wb_, f"{'ABCD'[j]}{4 + i}", val).border = BOX
header(wb_, 11, ["Row", "Weight", "What gets the points"])
weights = [
    ("Net benefit, year 1", 40, "Euros gained in the first 12 months after all costs"),
    ("ROI, year 1", 10, "Net benefit ÷ total cost"),
    ("Payback", 10, "Months until the money gained covers the money spent"),
    ("Evidence", 20, "0 = unsupported · 3 = real baseline from company data, tested on past data · 5 = measured live in the demo on real data, every input sourced"),
    ("Works", 20, "0 = slides only · 3 = core task works end to end on real data, with visible errors · 5 = normal, unusual and failure cases all work"),
]
for i, (a, b_, c) in enumerate(weights):
    put(wb_, f"A{12 + i}", a).border = BOX
    put(wb_, f"B{12 + i}", b_).border = BOX
    put(wb_, f"C{12 + i}", c, F(), None, None, wrap=True).border = BOX
put(wb_, "A18", "Points = rating ÷ 5 × weight. A Works rating below 3 means no prize. Tie: higher net benefit wins, then higher evidence.", NOTE)
wb_.column_dimensions["A"].width = 22
wb_.column_dimensions["B"].width = 22
wb_.column_dimensions["C"].width = 80
wb_.column_dimensions["D"].width = 28

# ---------------------------------------------------------------- Evidence


def text(val):
    if isinstance(val, dict):
        return " · ".join(f"{k} {text(x)}" for k, x in val.items())
    if isinstance(val, (int, float)) and not isinstance(val, bool):
        return f"{val:,}" if float(val).is_integer() else f"{val:,.4g}" if abs(val) < 1 else f"{val:,.2f}".rstrip("0").rstrip(".")
    return str(val)


we = wb.create_sheet("Evidence")
put(we, "A1", "Evidence: every figure in numbers.json", TITLE)
put(we, "A2", f"docs/hackathon-2026/evidence/numbers.json, generated {NUMBERS['generated']} by {NUMBERS['generator']}. "
              "Each row names the script that reruns it.", NOTE)
header(we, 3, ["ID", "Figure", "Value", "Unit", "Label", "Method", "Source", "Script"])
order = sorted(NUMBERS["figures"], key=lambda f: int(f["id"][1:]))
for i, f in enumerate(order):
    r = 4 + i
    for j, val in enumerate([f["id"], f["name"], text(f["value"]), f["unit"], text(f["label"]), f["method"], f["source"], f["script"]]):
        c = put(we, f"{'ABCDEFGH'[j]}{r}", val, NOTE if j in (5, 6, 7) else BOLD if j == 0 else BLACK, None, None, wrap=True)
        c.border = BOX
for col, w in zip("ABCDEFGH", (6, 40, 34, 14, 22, 60, 34, 34)):
    we.column_dimensions[col].width = w

for sheet in wb.worksheets:
    sheet.sheet_view.showGridLines = False
    sheet.freeze_panes = None

wb.save(OUT)
print("saved", OUT)
