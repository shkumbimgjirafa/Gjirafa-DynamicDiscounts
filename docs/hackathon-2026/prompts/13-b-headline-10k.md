# Prompt 13 (data session): framing B's headline is €10.4k a month; €4.4k is its controlled floor; €13k leaves the headline

**The user's decision** (data session, 31 Oct, about 10:30): "just 10k month make a strong case for it because we have it",
then "ok do it" to this plan. B0's headline is **E20 mid, €10,409 a month** (measured before/after). The controlled
**€4,402 stays, shown as the floor** under the claim, not as a scenario of equal weight. The **€13,000 season-adjusted
case leaves the headline**: it is quoted from the main repo, not recomputed here.

**Why the €4,402 stays visible:** it sits in `e20-test-phase.sql`, `numbers.json` and the live Evidence page. Dry run 2's
juror already scored framing B on it ("Net benefit 4 (B·Low only)"). A reader who finds a lower number we left out trusts
the rest less. Shown as the floor, with the reason it is a floor, it supports the claim.

## Do, committing with `dotnet test` green

1. **One B claim.**
   - Documents to change: the one-pager, `HACKATHON.md`, the financial sheet's Summary and Score, the Evidence page,
     the demo step 5 and the backup-video notes.
   - Framing B's claim = B0 €10,409 plus the levers' central case, which is today's B·Mid: €12,965 a month at full
     scope, 84 points.
   - Under it, one line: the controlled floor, €4,402, which is today's B·Low (€5,694 a month, 68 points).
   - Drop B·High from the headline and the score table, or relabel it as an upside that isn't claimed. Your call, as
     long as nothing presents €13,000 as part of the claim.
   - Framing A stays the conservative cross-check, unchanged.
   - Lever 4 (MK/AL) follows: the claim at E20 mid's rate, the floor at the controlled rate (€560).
2. **The case for €10,409**, in the documents' plain voice. Use only figures `numbers.json` already carries, each with
   its label:
   - **Measured on the real shop, not modelled.** Booked sales lines with booked COGS: Aug €11,068 and Sep €9,749 (Mall
     KS €8,551 / €7,461, Gj50 KS €2,517 / €2,288). Both Kosovo layers, both months.
   - **Scripted and live.** `evidence/e20-test-phase.sql` recomputes it from this repo's data. The Evidence page reruns
     it live as a read-only user (9 s).
   - **The data ties out.** The same sales lines reproduce the tool's own Profit30Before for every approval, most to the
     euro (`exports/README.md`, sanity check).
   - **€4,402 is a floor by design, for two reasons:**
     - It rests on one controlled run per layer (Mall #25 of 3 Sep, Gj50 #1), applied to both months.
     - Its comparison group is products the calculator itself kept at their price. Keeping a price is part of what the
       calculator decides, and shoppers moving from changed to kept products lift that group. So the comparison
       nets out part of the effect: Mall #25 measures €3,263 against the kept products and €4,088 against products
       not in the approval.
   - **Not controlled, and say so once:** "measured before and after each approval; the controlled floor is €4,402".
   - Don't cite the YoY fingerprint (E25) for B0, as Prompt 12 already decided. Don't quote August's own controlled
     run (#12) as support: restocking confounds it.
3. **The consistency test** (Prompt 12's): it now checks the B claim (E20 mid, B·Mid) and the floor (E20 low, B·Low) in
   `numbers.json`, the sheet's cached cells, the one-pager, `HACKATHON.md` and the Evidence page. It also checks that
   no document presents €13,000 as part of the claim. `numbers.json` keeps E20 high, labelled "quoted from the main
   repo, not recomputed, not part of the claim".
4. **AI-judge dry run 3's brief:** score framing B at the claim (B0 €10,409, €12,965 a month at full scope), with
   €4,402 as the disclosed controlled floor and framing A as the cross-check.

**Done when:**
- Every document gives B one headline (€10,409 B0, €12,965 a month, 84 points) with the €4,402 floor under it.
- €13,000 is nowhere in the claim.
- The consistency test passes, dry run 3's brief matches, `hours.md` is updated and `dotnet test` is green.
