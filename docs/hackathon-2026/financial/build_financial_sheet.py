from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.comments import Comment

OUT = r"C:/Projects/Gjirafa Dynamic Discounts/docs/hackathon-2026/Financial sheet (draft).xlsx"

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
PCT = '0.0%;(0.0%);"-"'
NUM1 = '0.0;(0.0);"-"'
NUM0 = '#,##0;(#,##0);"-"'

wb = Workbook()


def put(ws, ref, value, font=BLACK, fmt=None, fill=None, bold=False, wrap=False, comment=None):
    c = ws[ref]
    c.value = value
    c.font = Font(name=ARIAL, size=font.size, bold=bold or font.bold, italic=font.italic, color=font.color)
    if fmt:
        c.number_format = fmt
    if fill:
        c.fill = fill
    if wrap:
        c.alignment = Alignment(wrap_text=True, vertical="top")
    if comment:
        c.comment = Comment(comment, "Claude")
    return c


def header(ws, row, labels, start_col=1):
    for i, lab in enumerate(labels):
        c = ws.cell(row=row, column=start_col + i, value=lab)
        c.font = BOLD
        c.fill = HEAD
        c.border = BOX
        c.alignment = Alignment(wrap_text=True, vertical="center")


# ---------------------------------------------------------------- Summary
ws = wb.active
ws.title = "Summary"
put(ws, "A1", "Track A score: AI pricing and campaign agents (draft)", TITLE)
put(ws, "A2", "Draft 2026-10-29, made before the hackathon. Lever values in 'Inputs' are placeholders (yellow) "
              "until the weekend backtest replaces them; costs are planning assumptions until hours.md and the "
              "AI call log exist. All cells below are formulas.", NOTE, wrap=True)
ws.merge_cells("A2:G2")
ws.row_dimensions[2].height = 40
put(ws, "A3", "Framing A = only what the weekend build adds. Framing B = rollout of the test phase's measured rate "
              "(B0: Low = same-weeks control, Mid = measured before/after, High = season-adjusted) plus the new levers. "
              "The organisers decide which framing counts (ask at kickoff).", NOTE, wrap=True)
ws.merge_cells("A3:G3")
ws.row_dimensions[3].height = 30

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
for i, (label, f, fmt, font) in enumerate(rows):
    r = 6 + i
    put(ws, f"A{r}", label, BOLD if not label.startswith("  ") else BLACK)
    for c in cols:
        cell = put(ws, f"{c}{r}", f.format(c=c, l=lever_col[c], e=ev_row[c]), font, fmt)
        cell.border = BOX
    if label.startswith("Total"):
        for c in ["A"] + cols:
            ws[f"{c}{r}"].fill = TOTAL
            ws[f"{c}{r}"].font = Font(name=ARIAL, size=10, bold=True)
put(ws, "A23", "Reference: the worked example in the hackathon rules scores 76 / 100.", NOTE)
put(ws, "A24", "Evidence: framing B rates 5 because its main number was measured live on the real shop (two months of "
               "real prices and sales, booked COGS, same-weeks control) and the Evidence page recomputes it in the demo. "
               "Framing A's new levers are simulated on past data, so they rate 4.", NOTE, wrap=True)
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
    "Every input is labelled measured / estimated / assumed / placeholder with its source (see Inputs and Evidence).",
]):
    put(ws, f"A{34 + i}", "• " + t, F())
ws.column_dimensions["A"].width = 38
for c in cols:
    ws.column_dimensions[c].width = 13

# ---------------------------------------------------------------- Inputs
wi = wb.create_sheet("Inputs")
put(wi, "A1", "Inputs", TITLE)
put(wi, "A2", "Legend: blue = editable input · yellow fill = placeholder or key assumption to replace on the weekend · "
              "black = formula. Example: after the backtest, type the lever's low-case €/month into its Low column.",
    NOTE, wrap=True)
wi.merge_cells("A2:F2")
wi.row_dimensions[2].height = 30

header(wi, 4, ["Cost input", "Value", "Unit", "Label", "Source / note"])
cost_rows = [
    (5, "Hourly rate", 50, "€/h", "rule", "Hackathon rules: every person-hour costs €50 (planning rate; Finance can replace it)", False),
    (6, "Hackathon hours", 41, "h", "assumed", "Solo plan incl. remote evening blocks; replace with docs/hackathon-2026/hours.md", True),
    (7, "Remaining build to production", 120, "h", "assumed", "Go-live of the NOP discount writer, campaign agent hardening, monitoring, tests", True),
    (8, "Training hours", 4, "h", "assumed", "Hand-over to the pricing and campaign owners", False),
    (9, "Setup fees", 0, "€", "assumed", "Runs on the existing k8s cluster and databases", False),
    (10, "AI usage", 150, "€/month", "assumed", "Replace with the measured token log × projected weekly volume", True),
    (11, "Servers", 0, "€/month", "assumed", "Existing k8s cluster; no new infrastructure", False),
    (12, "Maintenance", 4, "h/month", "assumed", "Ongoing fixes and data checks", False),
    (13, "Exception review", 0.5, "h/week", "assumed", "Autonomous mode: a person only reviews held items, pauses and rollbacks", False),
    (14, "First live month", 2, "month #", "assumed", "Built and tested in month 1, live from month 2; Finance may argue month 3", True),
]
for r, lab, v, unit, labl, src, key in cost_rows:
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", v, BLUE, NUM1 if isinstance(v, float) else NUM0, YELLOW if key else None).border = BOX
    put(wi, f"C{r}", unit)
    put(wi, f"D{r}", labl)
    put(wi, f"E{r}", src, NOTE)
put(wi, "A15", "Months live in year 1")
put(wi, "B15", "=13-B14", BLACK, NUM0).border = BOX
put(wi, "C15", "months")
put(wi, "D15", "formula")

header(wi, 18, ["Benefit lever (€/month unless stated)", "Low", "Mid", "High", "Label", "Method / source"])
lev = [
    (19, "Cross-dock below-cost loss baseline", 2850, 2850, 2850, "measured", "SR × WMS check-ins, KS, Oct 2025–Sep 2026 (booked COGS)", False, EUR),
    (20, "Catch rate of the loss-sale block", 0.60, 0.75, 0.90, "estimated", "Share of loss lines whose price passes through the tool and its checker", True, PCT),
    (22, "2. Per-category floors", 500, 1500, 2500, "placeholder", "Backtest lever 2 (below-floor lines lifted, units × elasticity)", True, EUR),
    (23, "3. Cost reaction", 300, 800, 1500, "placeholder", "Backtest lever 3 (margin lost while price lagged a >5% cost rise)", True, EUR),
    (24, "4. MK/AL coverage", 0, 1000, 2000, "placeholder", "Backtest lever 4; check MK/AL push history first (MK/AL were pushed in Oct 2026)", True, EUR),
    (25, "5. Daily cadence", 0, 500, 1000, "placeholder", "Backtest lever 5 (gap between manual push dates)", True, EUR),
    (26, "6. Learning from past changes", 0, 300, 500, "placeholder", "Backtest lever 6", True, EUR),
    (27, "7. Campaign planning (campaign + pricing agents)", 500, 1500, 2500, "placeholder", "Backtest lever 7: replay Për ty Oct 2026 and Black Friday 2025, annualised over campaign days", True, EUR),
]
for r, lab, lo, mi, hi, labl, src, key, fmt in lev:
    put(wi, f"A{r}", lab)
    for col, v in zip("BCD", (lo, mi, hi)):
        put(wi, f"{col}{r}", v, BLUE, fmt, YELLOW if key else None).border = BOX
    put(wi, f"E{r}", labl)
    put(wi, f"F{r}", src, NOTE)
put(wi, "A21", "1. Loss-sale block")
for col in "BCD":
    put(wi, f"{col}21", f"={col}19*{col}20", BLACK, EUR).border = BOX
put(wi, "E21", "formula")
put(wi, "A28", "New benefit, framing A (sum of levers 1–7)", BOLD)
for col in "BCD":
    c = put(wi, f"{col}28", f"=SUM({col}21:{col}27)", BLACK, EUR)
    c.border = BOX
    c.fill = TOTAL
    c.font = Font(name=ARIAL, size=10, bold=True)

header(wi, 30, ["Framing B: test-phase rollout rate (B0), three measurements", "Value", "Unit", "Label", "Source / note"])
b0 = [
    (31, "Same-weeks control (changed vs kept SKUs)", 4000, "measured (floor)",
     "GjirafaMall KS run of 3 Sep only: +€115–141/day ≈ €3.5–4.3k/month. One run on one of the two KS layers, so a floor for the whole tool"),
    (32, "Before/after on approvals, KS, Aug–Sep average", 10632, "measured",
     "Aug €11,184 · Sep €10,079 (PricingTool.Approvals Profit30 windows, SKU-deduped, booked COGS from SR)"),
    (33, "Same, season-adjusted with 2025's same weeks", 13000, "estimated (upper)",
     "Aug €11.9k · Sep €13.8k. Layer-level GP growth did not beat 2025's seasonality, so this is the top of the range"),
]
for r, lab, v, labl, src in b0:
    put(wi, f"A{r}", lab)
    put(wi, f"B{r}", v, BLUE, EUR).border = BOX
    put(wi, f"C{r}", "€/month")
    put(wi, f"D{r}", labl)
    put(wi, f"E{r}", src, NOTE)
header(wi, 35, ["B0 used per scenario", "Low", "Mid", "High", "Label", "Why"])
put(wi, "A36", "B0 (€/month)", BOLD)
for col, src in zip("BCD", ("B31", "B32", "B33")):
    c = put(wi, f"{col}36", f"={src}", BLACK, EUR)
    c.border = BOX
    c.fill = TOTAL
put(wi, "E36", "formula")
put(wi, "F36", "Low = the controlled floor · Mid = the measured result · High = the season-adjusted result", NOTE)

header(wi, 38, ["Judgment ratings", "Low", "Mid", "High", "Label", "Meaning"])
put(wi, "A39", "Evidence rating, framing A (0–5)")
put(wi, "A40", "Evidence rating, framing B (0–5)")
put(wi, "A41", "Works rating (0–5)")
for col, ev_a, ev_b, wk in zip("BCD", (4, 4, 4), (5, 5, 5), (3, 4, 5)):
    put(wi, f"{col}39", ev_a, BLUE, "0", YELLOW).border = BOX
    put(wi, f"{col}40", ev_b, BLUE, "0", YELLOW).border = BOX
    put(wi, f"{col}41", wk, BLUE, "0", YELLOW).border = BOX
for r in (39, 40, 41):
    put(wi, f"E{r}", "judgment")
put(wi, "F39", "4 = measured baselines + backtest on real past data, recomputed live on the Evidence page; the new levers themselves are simulated, not measured live", NOTE)
put(wi, "F40", "5 = the rate was measured live on the real shop (Aug–Sep 2026: real prices, real sales, booked COGS, same-weeks control) and the Evidence page recomputes it in the demo from sourced exports", NOTE)
put(wi, "F41", "3 = works end to end with visible errors · 5 = normal, unusual and failure cases all work (below 3 = no prize)", NOTE)
wi.column_dimensions["A"].width = 46
for c in "BCD":
    wi.column_dimensions[c].width = 12
wi.column_dimensions["E"].width = 16
wi.column_dimensions["F"].width = 80
wi.column_dimensions["E"].width = 18

# ---------------------------------------------------------------- Financial case
wf = wb.create_sheet("Financial case")
put(wf, "A1", "Financial case: the Track A money formula", TITLE)
header(wf, 3, ["Line", "A · Low", "A · Mid", "A · High", "B · Low", "B · Mid", "B · High", "How it's calculated"])
benefit = {"B": "=Inputs!B28", "C": "=Inputs!C28", "D": "=Inputs!D28",
           "E": "=Inputs!B28+Inputs!B36", "F": "=Inputs!C28+Inputs!C36", "G": "=Inputs!D28+Inputs!D36"}
lines = [
    (4, "Monthly benefit (€/month)", None, EUR, GREEN, "Framing A: levers 1–7 · Framing B: + B0 (test-phase rollout rate)"),
    (5, "Months live in year 1", "=Inputs!$B$15", NUM0, GREEN, "Counting only the months it actually runs"),
    (6, "First-year benefit", "={c}4*{c}5", EUR, BLACK, "Extra margin in the first 12 months"),
    (7, "One-time cost", "=(Inputs!$B$6+Inputs!$B$7+Inputs!$B$8)*Inputs!$B$5+Inputs!$B$9", EUR, GREEN, "(Hackathon + remaining build + training hours) × €50 + setup fees"),
    (8, "Running cost (12 months)", "=12*(Inputs!$B$10+Inputs!$B$11+Inputs!$B$12*Inputs!$B$5)+52*Inputs!$B$13*Inputs!$B$5", EUR, GREEN, "12 × (AI + servers + maintenance hours × €50) + 52 × review hours × €50"),
    (9, "Total cost", "={c}7+{c}8", EUR, BLACK, "One-time + running"),
    (10, "Net benefit", "={c}6-{c}9", EUR, BLACK, "First-year benefit − total cost"),
    (11, "ROI", "=IF({c}9>0,{c}10/{c}9,0)", PCT, BLACK, "Net benefit ÷ total cost"),
    (12, "Running cost per month", "={c}8/12", EUR, BLACK, "Used for payback"),
    (13, "Payback (months)", '=IF({c}4<={c}12,"never",({c}7+{c}4*(Inputs!$B$14-1))/({c}4-{c}12))', NUM1, BLACK, "Month when cumulative gain covers cumulative spend (benefit from the first live month)"),
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
    for j, v in enumerate(row):
        put(wb_, f"{'ABCD'[j]}{4 + i}", v).border = BOX
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
we = wb.create_sheet("Evidence")
put(we, "A1", "Evidence behind the numbers (measured before the hackathon)", TITLE)
header(we, 3, ["Fact", "Value", "Label", "Method / source", "Measured on"])
facts = [
    ("Test phase (calculator v0.5), before/after on approvals, KS", "Aug €11,184 · Sep €10,079", "measured",
     "PricingTool.Approvals Profit30 windows, SKU-deduped, booked COGS (SR)", "2026-10-27"),
    ("Same, seasonally adjusted with 2025's same windows", "Aug €11.9k · Sep €13.8k · ≈ €13k/month", "estimated",
     "Each approval's 'before' scaled by 2025 GP/day over the same calendar windows; Aug gain carried into Sep", "2026-10-27"),
    ("Same-weeks control: changed vs kept SKUs, Sep run (GjirafaMall KS)", "+€115–141/day ≈ €3.5–4.3k/month", "measured",
     "ApprovalItems up/down/kept × SR daily GP, 30 days before vs after; price-up margin 18.4% → 24.5%", "2026-10-27"),
    ("Same-weeks control, Aug run", "not separable from restocking", "measured",
     "Units rose 285% after price rises; 86–97% of changed groups' after-GP from SKUs that didn't sell before", "2026-10-27"),
    ("Year-over-year margin, Kosovo (Mall + Gj50)", "+4–5 pp vs 2025 since the tool; +1–4 pp before", "measured",
     "SR monthly, tool scope (Aug 15.8% vs 11.7%; Sep 15.2% vs 10.2%)", "2026-10-27"),
    ("Cross-dock sales below cost, KS", "€2.85k/month (last 12 months); €145k over 24 months", "measured",
     "SR lines × WMS ProductCheckIns (check-in after order = cross-dock), booked COGS", "2026-10-27"),
    ("Profit forgone below the margin floor", "Jun €24.4k · Jul €15.1k", "measured",
     "SR lines vs per-band floors, booked COGS; reconciled with the Power BI model", "2026-07-31"),
    ("Kosovo gross-profit base", "≈ €125–135k/month", "measured",
     "Approval 30-day windows (Mall KS ≈ €70–76k, Gj50 KS ≈ €51k)", "2026-10-27"),
    ("MK/AL already pushed in October", "≈ 200k discounts each (oct-disc-mk-26 / oct-disc-al-26)", "measured",
     "NOP Discount table, both store DBs; affects lever 4", "2026-10-28"),
]
for i, row in enumerate(facts):
    for j, v in enumerate(row):
        c = put(we, f"{'ABCDE'[j]}{4 + i}", v, NOTE if j == 3 else BLACK, None, None, wrap=True)
        c.border = BOX
we.column_dimensions["A"].width = 44
we.column_dimensions["B"].width = 34
we.column_dimensions["C"].width = 12
we.column_dimensions["D"].width = 70
we.column_dimensions["E"].width = 12

for sheet in wb.worksheets:
    sheet.sheet_view.showGridLines = False
    sheet.freeze_panes = None

wb.save(OUT)
print("saved", OUT)
