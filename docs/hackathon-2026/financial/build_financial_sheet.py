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


built = datetime.now().strftime("%Y-%m-%d %H:%M")
snapshot = FIG["E1"]["source"]

# ---------------------------------------------------------------- Summary
ws = wb.active
ws.title = "Summary"
put(ws, "A1", "Track A score: AI pricing and campaign agents", TITLE)
put(ws, "A2", f"Built {built} by docs/hackathon-2026/financial/build_financial_sheet.py from evidence/numbers.json "
              f"(backtest: {snapshot}). Every input on 'Inputs' carries its numbers.json ID or its label (assumed / "
              "judgment); every cell below is a formula.", NOTE, wrap=True)
ws.merge_cells("A2:G2")
ws.row_dimensions[2].height = 40
put(ws, "A3", "Framing A = only what the hackathon build adds (levers 1–7, backtested). Framing B = the rollout of the "
              "calculator's test phase (B0) plus the same levers. Framing B claims no past euro: the registration said the "
              "calculator's measured Aug–Sep result is not claimed; B claims only the next 12 months of the automated "
              "system and uses that result as evidence for the rate. Which framing counts is the organisers' call.",
    NOTE, wrap=True)
ws.merge_cells("A3:G3")
ws.row_dimensions[3].height = 52

cols = ["B", "C", "D", "E", "F", "G"]
scen = ["A · Low", "A · Mid", "A · High", "B · Low", "B · Mid", "B · High"]
lever_col = {"B": "B", "C": "C", "D": "D", "E": "B", "F": "C", "G": "D"}  # scenario -> Inputs column
ev_row = {"B": 39, "C": 39, "D": 39, "E": 40, "F": 40, "G": 40}  # scenario -> Inputs evidence row (A / B)
header(ws, 5, ["Row (weight)"] + scen)
rows = [
    ("Monthly benefit (€/month)", "='Financial case'!{c}4", EUR, GREEN),
    ("Net benefit, year 1 (€)", "='Financial case'!{c}10", EUR, GREEN),
    ("  Net benefit rating (0–5)", "=IF({c}7>100000,5,IF({c}7>40000,4,IF({c}7>15000,3,IF({c}7>5000,2,IF({c}7>0,1,0)))))", "0", BLACK),
    ("  Net benefit points (40)", "={c}8/5*40", NUM0, BLACK),
    ("ROI, year 1", "='Financial case'!{c}11", PCT, GREEN),
    ("  ROI rating (0–5)", "=IF({c}10>2,5,IF({c}10>1,4,IF({c}10>0.5,3,IF({c}10>0.25,2,IF({c}10>0,1,0)))))", "0", BLACK),
    ("  ROI points (10)", "={c}11/5*10", NUM0, BLACK),
    ("Payback (months)", "='Financial case'!{c}13", NUM1, GREEN),
    ("  Payback rating (0–5)", "=IF(ISNUMBER({c}13),IF({c}13<=1,5,IF({c}13<=3,4,IF({c}13<=6,3,IF({c}13<=9,2,IF({c}13<=12,1,0))))),0)", "0", BLACK),
    ("  Payback points (10)", "={c}14/5*10", NUM0, BLACK),
    ("Evidence rating (0–5)", "=Inputs!{l}{e}", "0", GREEN),
    ("  Evidence points (20)", "={c}16/5*20", NUM0, BLACK),
    ("Works rating (0–5)", "=Inputs!{l}41", "0", GREEN),
    ("  Works points (20)", "={c}18/5*20", NUM0, BLACK),
    ("Total (/100)", "={c}9+{c}12+{c}15+{c}17+{c}19", NUM0, BLACK),
    ("Prize eligible (Works ≥ 3)", '=IF({c}18>=3,"Yes","No")', None, BLACK),
]
for i, (lab, f, fmt, font) in enumerate(rows):
    r = 6 + i
    put(ws, f"A{r}", lab, BOLD if not lab.startswith("  ") else BLACK)
    for c in cols:
        cell = put(ws, f"{c}{r}", f.format(c=c, l=lever_col[c], e=ev_row[c]), font, fmt)
        cell.border = BOX
    if lab.startswith("Total"):
        for c in ["A"] + cols:
            ws[f"{c}{r}"].fill = TOTAL
            ws[f"{c}{r}"].font = Font(name=ARIAL, size=10, bold=True)
put(ws, "A23", "Reference: the worked example in the hackathon rules scores 76 / 100.", NOTE)
put(ws, "A24", "Evidence and Works are judgment ratings (Inputs rows 39–41), not formulas; their reasons are next to them. "
               "Framing A's levers are backtested on real past sales lines with every input sourced; framing B's rate was "
               "measured on the real shop before the hackathon, in the main repo, and is not recomputed by this build.",
    NOTE, wrap=True)
ws.merge_cells("A24:G24")
ws.row_dimensions[24].height = 30

put(ws, "A25", "Monthly benefit needed for each net-benefit rating, with these costs", BOLD)
header(ws, 26, ["Net-benefit rating", "€ per month needed"])
for i, (lab, thr) in enumerate([("2 (net > €5k)", 5000), ("3 (net > €15k)", 15000),
                                ("4 (net > €40k)", 40000), ("5 (net > €100k)", 100000)]):
    r = 27 + i
    put(ws, f"A{r}", lab)
    put(ws, f"B{r}", f"=({thr}+'Financial case'!$B$9)/'Financial case'!$B$5", BLACK, EUR).border = BOX
put(ws, "A31", "Costs are the same in every scenario; benefit starts in the first live month (Inputs B14).", NOTE)

put(ws, "A33", "Rules that apply (hackathon Track A)", BOLD)
for i, t in enumerate([
    "Count margin, not revenue. All benefit here is gross profit on own-retail SKUs (no marketplace seller sales).",
    "Hours saved are capacity, not money, so they are not counted as benefit.",
    "Every person-hour costs €50, including the hackathon hours.",
    "Every input is labelled measured / estimated / assumed / judgment with its source (see Inputs and Evidence).",
]):
    put(ws, f"A{34 + i}", "• " + t, F())
ws.column_dimensions["A"].width = 38
for c in cols:
    ws.column_dimensions[c].width = 13

# ---------------------------------------------------------------- Inputs
wi = wb.create_sheet("Inputs")
put(wi, "A1", "Inputs", TITLE)
put(wi, "A2", "Legend: blue = input typed from numbers.json (ID in the last column) or an assumption · yellow fill = a "
              "key assumption or judgment worth challenging · black = formula.", NOTE, wrap=True)
wi.merge_cells("A2:G2")
wi.row_dimensions[2].height = 30

attended = v("E16", "attendedHours")
build_usd = v("E18", "sinceKickoffUsd")
header(wi, 4, ["Cost input", "Value", "Unit", "Label", "Source / note", "", "ID"])
cost_rows = [
    (5, "Hourly rate", 50, "€/h", "rule", "Hackathon rules: every person-hour costs €50", False, ""),
    (6, "Hackathon hours (attended)", attended, "h", label("E16"),
     f"hours.md, attended rows only ({v('E16', 'rows')} rows; parallel sessions counted once). The "
     f"{v('E16', 'unattendedBlocks')} unattended blocks are AI cost (row 16), not person-hours", False, "E16"),
    (7, "Remaining build to production", 120, "h", "assumed",
     "The live shop write-back (a write-enabled login, canary on a few categories), start-up as a service, the weekly "
     "email, MK/AL data loads, Nova hardening, monitoring", True, ""),
    (8, "Training hours", 4, "h", "assumed", "Hand-over to the pricing and campaign owners", False, ""),
    (9, "Setup fees", 0, "€", "assumed", "Runs on the existing k8s cluster and databases", False, ""),
    (10, "AI usage (agents' runtime calls)", "=D49*B17", "€/month", "estimated",
     "Rows 44–48: measured cost per call (E19) × assumed calls a month, at the API-equivalent price", False, "E19"),
    (11, "Servers", 0, "€/month", "assumed", "Existing k8s cluster; no new infrastructure", False, ""),
    (12, "Maintenance", 4, "h/month", "assumed", "Ongoing fixes and data checks", False, ""),
    (13, "Exception review", 0.5, "h/week", "assumed",
     "Autonomous mode: a person reviews only held items, pauses and rollbacks (the plan's 15–30 min a week)", False, ""),
    (14, "First live month", 2, "month #", "assumed", "Built and tested in month 1, live from month 2; Finance may argue month 3", True, ""),
    (16, "Build sessions' AI usage, whole build", build_usd, "USD", label("E18"),
     f"The two Claude Code sessions since kickoff, {v('E18', 'sinceKickoffResponses')} responses; "
     f"${v('E18', 'unattendedBlocksUsd')} of it in the unattended blocks. API-equivalent: the actual spend is the "
     "flat Claude subscription, so this overstates the cash cost", False, "E18"),
    (17, "USD to EUR", 1.0, "€ per $", "assumed", "1:1, which overstates the dollar costs in euros slightly (conservative)", False, ""),
]
for r, lab, val, unit, labl, src, key, fid in cost_rows:
    put(wi, f"A{r}", lab)
    is_formula = isinstance(val, str) and val.startswith("=")
    fmt = EUR if unit.startswith("€/m") else USD if unit == "USD" else NUM2 if r == 17 else NUM1 if isinstance(val, float) else NUM0
    put(wi, f"B{r}", val, BLACK if is_formula else BLUE, fmt, YELLOW if key else None).border = BOX
    put(wi, f"C{r}", unit)
    put(wi, f"D{r}", labl)
    put(wi, f"E{r}", src, NOTE)
    put(wi, f"G{r}", fid, BOLD)
put(wi, "A15", "Months live in year 1")
put(wi, "B15", "=13-B14", BLACK, NUM0).border = BOX
put(wi, "C15", "months")
put(wi, "D15", "formula")

header(wi, 18, ["Benefit lever (€/month unless stated)", "Low", "Mid", "High", "Label", "Method / source", "ID"])
put(wi, "A19", "Cross-dock lines sold under booked cost (baseline)")
for col in "BCD":
    put(wi, f"{col}19", v("E14"), BLUE, EUR).border = BOX
put(wi, "E19", label("E14"))
put(wi, "F19", FIG["E14"]["method"], NOTE)
put(wi, "G19", "E14", BOLD)
put(wi, "A20", "Share of it the loss-sale block catches")
for col in "BCD":
    put(wi, f"{col}20", f"=IF({col}19>0,{col}21/{col}19,0)", BLACK, PCT).border = BOX
put(wi, "E20", "formula")
put(wi, "F20", "Lever 1 ÷ baseline: most losses come with a cost that jumped at that sale, which a known-cost rule can't see", NOTE)
cases = {"B": "low", "C": "central", "D": "high"}
lev = [
    (21, "1. Loss-sale block", "E1"),
    (22, "2. Per-category floors", "E2"),
    (23, "3. Cost reaction", "E3"),
    (24, "4. MK/AL coverage", "E4"),
    (25, "5. Daily cadence", "E5"),
    (26, "6. Learning from past changes (as built)", "E6"),
    (27, "7. Campaign planning (campaign + pricing agents)", "E7"),
]
for r, lab, fid in lev:
    put(wi, f"A{r}", lab)
    for col, case in cases.items():
        key = "mid" if fid == "E4" and case == "central" else case
        put(wi, f"{col}{r}", v(fid, key), BLUE, EUR).border = BOX
    put(wi, f"E{r}", label(fid))
    put(wi, f"F{r}", FIG[fid]["method"], NOTE)
    put(wi, f"G{r}", fid, BOLD)
put(wi, "A28", "New benefit, framing A (sum of levers 1–7)", BOLD)
for col in "BCD":
    c = put(wi, f"{col}28", f"=SUM({col}21:{col}27)", BLACK, EUR)
    c.border = BOX
    c.fill = TOTAL
    c.font = Font(name=ARIAL, size=10, bold=True)
put(wi, "E28", "formula")
put(wi, "F28", "Low = elasticity −2, Mid = −1, High = −0.5; each sales line counted by one lever", NOTE)
put(wi, "A29", "Check: the same total in numbers.json")
for col, case in cases.items():
    put(wi, f"{col}29", v("E8", case), BLUE, EUR).border = BOX
put(wi, "E29", '=IF(AND(B28=B29,C28=C29,D28=D29),"matches","DIFFERS")', BLACK)
put(wi, "G29", "E8", BOLD)

header(wi, 30, ["Framing B: the calculator's test phase (B0), three cases", "Value", "Unit", "Label", "Source / note", "", "ID"])
b0 = [
    (31, "Same-weeks control (changed vs unchanged products)", "low",
     "GjirafaMall KS run of 3 Sep only: +€115–141/day ≈ €3.5–4.3k/month. A test phase with a control, not a randomized "
     "test; one run on one of the two KS layers, so a floor for the whole tool"),
    (32, "Before/after on the approvals, KS, Aug–Sep average", "mid",
     "Aug €11,184 · Sep €10,079 (Approvals' 30-day profit windows, SKU-deduped, booked COGS). Not controlled"),
    (33, "Same, season-adjusted with 2025's same weeks", "high",
     "Aug €11.9k · Sep €13.8k. Not controlled; layer-level GP growth did not beat 2025's seasonality, so this is the top "
     "of the range"),
]
for r, lab, case, src in b0:
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", v("E20", case), BLUE, EUR).border = BOX
    put(wi, f"C{r}", "€/month")
    put(wi, f"D{r}", label("E20", case))
    put(wi, f"E{r}", src, NOTE)
    put(wi, f"G{r}", "E20", BOLD)
header(wi, 35, ["B0 used per scenario", "Low", "Mid", "High", "Label", "Why"])
put(wi, "A36", "B0 (€/month)", BOLD)
for col, src in zip("BCD", ("B31", "B32", "B33")):
    c = put(wi, f"{col}36", f"={src}", BLACK, EUR)
    c.border = BOX
    c.fill = TOTAL
put(wi, "E36", "formula")
put(wi, "F36", "Low = the controlled floor · Mid = the before/after result · High = the season-adjusted result", NOTE)

header(wi, 38, ["Judgment ratings", "Low", "Mid", "High", "Label", "Meaning"])
put(wi, "A39", "Evidence rating, framing A (0–5)")
put(wi, "A40", "Evidence rating, framing B (0–5)")
put(wi, "A41", "Works rating (0–5)")
for col, ev_a, ev_b, wk in zip("BCD", (4, 4, 4), (4, 4, 4), (3, 4, 5)):
    put(wi, f"{col}39", ev_a, BLUE, "0", YELLOW).border = BOX
    put(wi, f"{col}40", ev_b, BLUE, "0", YELLOW).border = BOX
    put(wi, f"{col}41", wk, BLUE, "0", YELLOW).border = BOX
for r in (39, 40, 41):
    put(wi, f"E{r}", "judgment")
put(wi, "F39", "Above 3 (real baseline, tested on past data): every lever is a walk-forward backtest on booked sales lines with "
               "booked COGS, every input has an ID and a script, and the Backtest page reruns it (about a minute). Not 5: "
               "the levers are simulated, not measured live", NOTE)
put(wi, "F40", "The rate was measured on the real shop (real prices and sales, booked COGS), the low case with a same-weeks "
               "control. Not 5: it was measured before the hackathon in the main repo and this build doesn't recompute it; "
               "the mid and high cases are before/after, not controlled", NOTE)
put(wi, "F41", "3 = works end to end with visible errors · 5 = normal, unusual and failure cases all work (below 3 = no "
               "prize); failure-cases.md lists the cases tested", NOTE)

header(wi, 43, ["AI running cost (agents' calls)", "USD per call", "Calls a month", "USD a month", "Label", "Volume assumption", "ID"])
e19 = v("E19")
ai_rows = [
    (44, "Run check (Sonnet 5.5)", "runCheck", 183, "one per engine run: 6 layers × 30.5 days"),
    (45, "Alpha, weekly floors and targets (Opus 5.5)", "alphaWeeklyPass", 26, "one per layer a week: 6 × 4.33"),
    (46, "Nova, weekly campaign briefs (Opus 5.5)", "novaWeeklyPass", 26, "one per layer a week: 6 × 4.33"),
    (47, "Campaign planning round (Alpha + Nova)", "planningCall", 12, "about 3 campaigns a month × 2 rounds × 2 calls"),
    (48, "Chat (Nova or Alpha)", "chat", 43, "about 10 questions a week"),
]
for r, lab, kind, calls, why in ai_rows:
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", e19[kind]["avgUsd"], BLUE, USD4).border = BOX
    put(wi, f"C{r}", calls, BLUE, NUM0, YELLOW).border = BOX
    put(wi, f"D{r}", f"=B{r}*C{r}", BLACK, USD).border = BOX
    put(wi, f"E{r}", "measured × assumed")
    put(wi, f"F{r}", f"{why}; cost per call averaged over {e19[kind]['calls']} logged calls", NOTE)
    put(wi, f"G{r}", "E19", BOLD)
put(wi, "A49", "AI usage a month (USD)", BOLD)
c = put(wi, "D49", "=SUM(D44:D48)", BLACK, USD)
c.border = BOX
c.fill = TOTAL
put(wi, "E49", "formula")
put(wi, "F49", "API-equivalent; through the team's Claude subscription the cash cost is the subscription itself", NOTE)

wi.column_dimensions["A"].width = 46
for c in "BCD":
    wi.column_dimensions[c].width = 12
wi.column_dimensions["E"].width = 22
wi.column_dimensions["F"].width = 80
wi.column_dimensions["G"].width = 6

# ---------------------------------------------------------------- Financial case
wf = wb.create_sheet("Financial case")
put(wf, "A1", "Financial case: the Track A money formula", TITLE)
header(wf, 3, ["Line", "A · Low", "A · Mid", "A · High", "B · Low", "B · Mid", "B · High", "How it's calculated"])
benefit = {"B": "=Inputs!B28", "C": "=Inputs!C28", "D": "=Inputs!D28",
           "E": "=Inputs!B28+Inputs!B36", "F": "=Inputs!C28+Inputs!C36", "G": "=Inputs!D28+Inputs!D36"}
lines = [
    (4, "Monthly benefit (€/month)", None, EUR, GREEN, "Framing A: levers 1–7 · Framing B: + B0 (the test phase's rate)"),
    (5, "Months live in year 1", "=Inputs!$B$15", NUM0, GREEN, "Counting only the months it actually runs"),
    (6, "First-year benefit", "={c}4*{c}5", EUR, BLACK, "Extra margin in the first 12 months"),
    (7, "One-time cost", "=(Inputs!$B$6+Inputs!$B$7+Inputs!$B$8)*Inputs!$B$5+Inputs!$B$9+Inputs!$B$16*Inputs!$B$17", EUR, GREEN,
     "(Hackathon + remaining build + training hours) × €50 + setup fees + the build sessions' AI usage"),
    (8, "Running cost (12 months)", "=12*(Inputs!$B$10+Inputs!$B$11+Inputs!$B$12*Inputs!$B$5)+52*Inputs!$B$13*Inputs!$B$5", EUR, GREEN,
     "12 × (AI + servers + maintenance hours × €50) + 52 × review hours × €50"),
    (9, "Total cost", "={c}7+{c}8", EUR, BLACK, "One-time + running"),
    (10, "Net benefit", "={c}6-{c}9", EUR, BLACK, "First-year benefit − total cost"),
    (11, "ROI", "=IF({c}9>0,{c}10/{c}9,0)", PCT, BLACK, "Net benefit ÷ total cost"),
    (12, "Running cost per month", "={c}8/12", EUR, BLACK, "Used for payback"),
    (13, "Payback (months)", '=IF({c}4<={c}12,"never",({c}7+{c}4*(Inputs!$B$14-1))/({c}4-{c}12))', NUM1, BLACK,
     "Month when cumulative gain covers cumulative spend (benefit from the first live month)"),
]
for r, lab, f, fmt, font, how in lines:
    put(wf, f"A{r}", lab, BOLD if r in (6, 9, 10) else BLACK)
    for c in cols:
        formula = benefit[c] if r == 4 else f.format(c=c)
        put(wf, f"{c}{r}", formula, font, fmt).border = BOX
    put(wf, f"H{r}", how, NOTE)
for c in ["A"] + cols:
    wf[f"{c}10"].fill = TOTAL
wf.column_dimensions["A"].width = 28
for c in cols:
    wf.column_dimensions[c].width = 12
wf.column_dimensions["H"].width = 70

# ---------------------------------------------------------------- Payback
wp = wb.create_sheet("Payback")
put(wp, "A1", "Payback: cumulative net position by month", TITLE)
put(wp, "A2", "Cumulative gain − cumulative spend. One-time cost lands in month 1; benefit starts in the first live month.", NOTE)
header(wp, 4, ["Month"] + scen)
for m in range(1, 13):
    r = 4 + m
    put(wp, f"A{r}", m, BLACK, NUM0)
    for c in cols:
        f = (f"='Financial case'!{c}$4*MAX(0,$A{r}-(Inputs!$B$14-1))"
             f"-('Financial case'!{c}$7+'Financial case'!{c}$12*$A{r})")
        put(wp, f"{c}{r}", f, BLACK, EUR).border = BOX
put(wp, "A18", "Payback month", BOLD)
for c in cols:
    put(wp, f"{c}18", f'=IF(COUNTIF({c}5:{c}16,"<0")>=12,"after 12",COUNTIF({c}5:{c}16,"<0")+1)', BLACK, NUM0).border = BOX
wp.column_dimensions["A"].width = 16
for c in cols:
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
for i, (a, b, c) in enumerate(weights):
    put(wb_, f"A{12 + i}", a).border = BOX
    put(wb_, f"B{12 + i}", b).border = BOX
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
r0 = 4 + len(order) + 2
put(we, f"A{r0}", "Measured before the hackathon in the main repo (quoted, not recomputed by this build)", BOLD)
header(we, r0 + 1, ["", "Fact", "Value", "", "Label", "Method / source", "Measured on"])
facts = [
    ("Test phase (calculator v0.5), before/after on approvals, KS", "Aug €11,184 · Sep €10,079", "measured, not controlled",
     "PricingTool.Approvals 30-day profit windows, SKU-deduped, booked COGS (sales lines)", "2026-10-27"),
    ("Same, seasonally adjusted with 2025's same windows", "Aug €11.9k · Sep €13.8k · ≈ €13k/month", "estimated, not controlled",
     "Each approval's 'before' scaled by 2025 GP/day over the same calendar windows; Aug gain carried into Sep", "2026-10-27"),
    ("Same-weeks control: changed vs unchanged products, Sep run (GjirafaMall KS)", "+€115–141/day ≈ €3.5–4.3k/month", "measured",
     "Approval items up/down/kept × daily GP, 30 days before vs after; price-up margin 18.4% → 24.5%", "2026-10-27"),
    ("Same-weeks control, Aug run", "not separable from restocking", "measured",
     "Units rose 285% after price rises; 86–97% of changed groups' after-GP from SKUs that didn't sell before", "2026-10-27"),
    ("Year-over-year margin, Kosovo (Mall + Gj50)", "+4–5 pp vs 2025 since the tool; +1–4 pp before", "measured",
     "Sales lines monthly, tool scope (Aug 15.8% vs 11.7%; Sep 15.2% vs 10.2%)", "2026-10-27"),
    ("Profit forgone below the margin floor", "Jun €24.4k · Jul €15.1k", "measured",
     "Sales lines vs per-band floors, booked COGS; reconciled with the Power BI model (E21)", "2026-07-31"),
]
for i, row in enumerate(facts):
    r = r0 + 2 + i
    for j, val in zip("BCEFG", row):
        put(we, f"{j}{r}", val, NOTE if j == "F" else BLACK, None, None, wrap=True).border = BOX
for col, w in zip("ABCDEFGH", (6, 40, 34, 14, 22, 60, 34, 34)):
    we.column_dimensions[col].width = w

for sheet in wb.worksheets:
    sheet.sheet_view.showGridLines = False
    sheet.freeze_panes = None

wb.save(OUT)
print("saved", OUT)
