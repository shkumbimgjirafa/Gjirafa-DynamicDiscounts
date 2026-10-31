# AI-judge dry run 3b (31 Oct 2026, 10:33–10:41)

Dry run 3 scored framing B as three cases before Prompt 13 made B0 €10,409 the headline. This rerun tests the headline:
the same juror on `f2de134` (Prompt 13 and dry run 3's fixes in). What it found and what we did. Monday 06:00's run stays
the final one.

## How it ran

The same isolation as dry runs 2 and 3: Claude Code headless, `claude-opus-5-5`, `--setting-sources ""`,
`--no-session-persistence`, no `CLAUDE*` / `ANTHROPIC_*` / `MCP_*` variables, Read / Grep / Glob and read-only git only,
`--max-budget-usd 15`; a fresh clone (no remote), the rules summary and a fresh cell-by-cell dump of the financial sheet.
The brief is dry run 3's with two changes: item 1 says the claim is framing B at its headline (B0 €10,409 a month, E20
mid; €12,965 a month at full scope), with the controlled measurement, €4,402 (E20 low), disclosed under it and framing A
as the cross-check; it asks for B scored at the claim and at the controlled measurement, and whether it accepts €10,409
as the rate and, if not, what evidence in the repository would change its mind. Item 7 asks it to judge dry run 3's
findings. 49 turns, 476 s, $3.65 at API list price. Its full report:
[`dry-run-3b-juror-report.md`](dry-run-3b-juror-report.md).

## Scores

| Row (weight) | Dry run 2 (B · Mid) | Dry run 3 (B) | **3b: B at the claim** | **3b: B at the controlled measurement** | Dry run 3 (A) | **3b: A, cross-check** |
|---|---|---|---|---|---|---|
| Net benefit (40) | 4 | 3 | 4 | 4 | 2 | 2 |
| ROI (10) | 5 | 4 | 5 | 5 | 3 | 3 |
| Payback (10) | 3 | 3 | 3 | 3 | 2 | 2 |
| Evidence (20) | 1 | 2 | 2 | 2 | 2 | 2 |
| Works (20) | 2 (3) | 2 (3) | 2 (3) | 2 (3) | 2 (3) | 2 (3) |
| **Total** | **60 (64)** | **54 (58)** | **64 (72)** | **64 (72)** | **42 (46)** | **42 (46)** |

Our sheet: B · Mid 84, B · Low 68, A · Mid 66. Bracketed: Works 3 if a full Run week on real data is shown live. At the
claim it scored net benefit "granted for argument"; the 5 needs €12,206 a month and the claim is €12,965, 6 % above it,
so it gives 4. At the controlled measurement it held the levers at their central case (our B · Low also drops them to
elasticity −2), which also lands in band 4.

## Does it accept €10,409 as the rate? No. Its reasons, word for word

1. "The team's own controlled run on the same approval gives about half." GjirafaMall's September before/after is
   €7,461; the controlled run on approval #25 gives €3,263 against the products it kept and €4,088 against products not
   in the approval. "The 'shoppers move to kept products' argument … doesn't explain the gap: the not-in-approval control
   has no such link to the changed products and still gives only €4,088."
2. "August rests on approval #12, which the team's own material calls confounded" (not separable from restocking), "yet
   #12's before/after is the bulk of August's €8,551."
3. "Gjirafa50's B0 is not measured in August and September. Approval #1's 30-day lift around 21 July is spread over
   21 Jul–6 Oct."
4. "The figures' own method text still says the year-over-year check 'does not support the uncontrolled two'."

**What would change its mind** (its list): the per-approval output of `e20-test-phase.sql` committed, showing how much
comes from the dead-stock sets (#6, #7, #13, #19, #22) and from #12; a restock-robust control for #12 (only SKUs with
stock and sales in both windows); controlled runs for the October approvals and for a Gjirafa50 approval inside Aug–Sep
that together approach €10k a month; above all, the pilot's holdout difference-in-differences from the money ledger; and,
to count B0 as new benefit at all, evidence that without v2 the calculator's pushes would stop.

The headline is the user's framing call, and it doesn't change here. The first item of that list is now in
`numbers.json` (E20's months by approval, below); the rest is new measurement, not a defect fix.

## What it found and what we did

| # | Severity | Finding | Outcome |
|---|---|---|---|
| 9, 18, 26, Biggest assumption | high | B0 is the declared calculator's money (DECLARATION.md says its effect "is not claimed"); nothing shows it is lost without v2 | **For the user** (handoff). `DECLARATION.md` stays as written; the user is asking about framing B |
| 10, 11, 38 | high, medium | The headline uses an uncontrolled rate the controls cut roughly in half, and E20's method text says the year-over-year check doesn't support it; the headline moved up after the last review with no new evidence | **For the user** (the headline is their decision). Fixed the claims that were wrong: "measured on the real shop, not modelled" is now "measured from the shop's booked sales lines, before and after each approval", with August's reliance on #12 and Gjirafa50's July lift stated (below) |
| 14 | medium | Gjirafa50's "Aug €2,517 / Sep €2,288" presented as measured in those months; they carry approval #1's July lift | **Fixed** wording in the one-pager and `HACKATHON.md`; E20 now lists each month by approval (`byMonth.byApproval`: approval, days live, lift a day) |
| 12 | medium | "The same sales lines reproduce the tool's own 30-day profit … most to the euro" (3 of 8 exact); and a tie-out checks the data, not the cause | **Fixed**: "within 2 % for each of eight approvals (E26), which checks the data, not the cause" |
| 13 | medium | `kickoff-plan.md:44` still says "only in KS… nothing secures that profit" | **Fixed** with a dated note (the plan is history) |
| 16 | low | "38 unattended blocks" vs E16 | **Fixed**: `HACKATHON.md` points at E16 instead of a count that goes stale |
| 17 | medium | Commit 1's list leaves out the docx and `CLAUDE.md` | **Fixed**: the list names every file in it |
| 27 | high | The net-benefit 5 is fragile: 6 % headroom | **Kept**: stated in the scores above; the pilot's assumptions are not tuned (Prompt 12) |
| 28 | medium | B0's construction (each approval's step over the previous one's prices; Gjirafa50 carried from July; dead-stock sets included) | **Disclosed** through E20's months by approval |
| 33 | low | In the dead-stock lane a model fix may be below cost (down to 40 % of cost) | **Kept**: dead stock is the one lane allowed below the floor (age tiers 100 / 80 / 60 / 40 % of cost), and the fix is re-checked by the rules |
| 1–8, 15, 20–24, 29–32, 34–37, 39–41 | | Not verifiable without the database; chosen controls; transcripts; rulings; the person's time; labels; the AI's share of the euros; reproducibility vs validity | **Kept / disclosed** as in dry runs 2 and 3 |

Of dry run 3's findings: 7 resolved, 4 partly, 10 disclosed only, the rest not resolved, including every high finding
about framing B. `dotnet test`: 967 passed, 1 skipped.
