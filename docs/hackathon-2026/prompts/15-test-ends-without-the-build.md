# Prompt 15 (data session): the case for B, told as the user means it. The test proved the engine; the build makes it permanent

**The user's position, in their own words** (data session, 31 Oct):
- "its not really live and mergim would agree the price engine works and can bring 10K"
- "if i woudl leave gjirafa price engine wouldnt continue but if in this hackathon we build this the price engine would be permanent"
- "controlled and dynamic"

When the data session restated it, the user answered "yess". The restatement:
1. The price engine isn't a live business process. It's a test the user has run by hand since July: no schedule, no owner but them, nothing that carries on if they stop.
2. The test proved the approach works (Aug–Sep).
3. Without this build, that money doesn't continue: the test ends.
4. The build turns the test into a real system: permanent, controlled and dynamic.
5. So the build's value is the stream it makes permanent, not only the extra levers.

This replaces the "B0 is the non-AI calculator's money" answer from Prompts 12 and 13. Framing B stays the claim at
today's figures (B0 €10,409, €12,965 a month at full scope). Framing A stays the cross-check.

**Rules for the wording:**
- Write facts, not other people's opinions. **No "Mergim would agree" and no names** for what happens without the
  build.
- The rate is evidence, not the argument: put it in a footnote-sized paragraph, with every check disclosed.

## Do, committing with `dotnet test` green

1. **Two proofs, presented apart**: in the one-pager, `HACKATHON.md`, the sheet's Summary, the Evidence page and demo
   step 5.
   - **Proof the engine works:** the Aug–Sep test on the real shop (E20).
   - **Proof it runs by itself:** this build. Run week on Gjirafa50 KS shown live on Monday, on fresh data: proposed
     prices, the check, the publish to the local sandbox with its rollback, the read-back. Every number traced to a
     query.
   - Mergca's reading of the Evidence row: Aug–Sep reaches 3; a 5 needs the live run with every input sourced.
2. **The without-the-build line**, one row in the sheet's Inputs and one sentence in the documents. Label it
   **assumed**, with the measured facts beside it. The facts:
   - **Single operator, no schedule, no owner.** Runs by hand since 21 Jul.
   - **GjirafaMall KS** runs: 21 Jul, 6 Aug, 3 Sep, 6 Oct, 19 Oct.
   - **Gjirafa50 KS:** regular prices set on 21 Jul and **left unrefreshed until 7 Oct** (78 days). Dead-stock markdowns
     ran on 3 Aug, 19 Aug and 3 Sep.
   - **MK/AL:** in Aug–Sep only GjirafaMall AL had a full push (1–2 Sep). GjirafaMall MK and Gjirafa50 MK got their
     first full push on 21 Oct (`hk.PushDatesMkAl`).
   - **Kosovo push sets** (`hk.PushDatesKs`, new, see below) show how long each push stayed live.
   - **Even when it ran, part of the effect was lost:** 19 % of the units on raised products still sold at the old price
     in Aug–Sep (E23). That was the normal case. Say that late October's 70–84 % was the month two push sets
     overlapped, not the typical leakage.
   - The assumption itself: "without automation, the test ends: no one runs the engine by hand as a permanent
     process".
3. **Fix the Gjirafa50 wording** Mergca flagged ("both Kosovo shops tested" vs "78 days without a run"):
   - Gjirafa50 KS's Aug–Sep figure is mostly its 21 July run carried forward, plus the three dead-stock runs.
   - Show B0 split by shop wherever it's quoted: E20 mid, Mall €8,006 avg and Gjirafa50 €2,403 avg.
4. **The rate's footnote: one paragraph, all checks disclosed.** New evidence E27 with the script
   **`evidence/e20-aug-sep-comparison.sql`** (committed by the data session; about 9 s; reads only
   PricingToolHackathon; add it to the Evidence page's live set). On 31 Oct, Aug–Sep average, € a month:

   | | Before/after (repriced SKUs, campaign days out) | Vs untouched SKUs, same shop and category, same weeks | The same, SKUs that sold in both windows |
   |---|---|---|---|
   | GjirafaMall KS | 10,308 | 10,596 | 2,611 |
   | Gjirafa50 KS | 2,219 | 1,222 | 138 |
   | Total | 12,527 | 11,818 | 2,749 |

   - **September separates cleanly:** GjirafaMall KS €3,881 vs untouched, €4,303 on SKUs that sold in both windows.
     That's close to the controlled €4,402 (E20 low).
   - **August's gap is restocking:** run #12 repriced 1,244 SKUs that sold in either window, only 45 in both.
   - **The gain is price:** on run #25's SKUs that sold in both windows, gross profit per unit rose 34 % (€6.61 → €8.84)
     with units down 8 %, against +16 % and −18 % on untouched SKUs.
   - Wording: "measured before and after each approval; against untouched products in the same category it holds
     (€11.8k) when every product counts, and it is €2.7k when only products that sold in both windows count (August's
     run coincided with restocking); September alone gives €3.9–4.3k for GjirafaMall".
   - The rate stays the claim's input as the user decided. The footnote is there so no reader finds a number we left
     out.
5. **Mergca's split benefit table**: one table in the sheet and the one-pager. Each line labelled measured, estimated or
   assumed, with its source:
   - the stream the build makes permanent (B0; rate measured, "ends without the build" assumed);
   - recovered leakage (lever 8);
   - cadence (lever 5);
   - MK/AL coverage (lever 4, at Kosovo's tested rate; show the €560 at the controlled rate next to it and say MK/AL had
     no two-month test of their own);
   - the other levers;
   - hours saved, as hours and not euros (the sheet already says they aren't benefit).
   Also add these:
   - **Marketplace:** "GjirafaMall lines are Gjirafa's own vendors only, at booked cost: no marketplace commission
     applies".
   - **Returns:** "cancellations are excluded; returns after delivery are not netted out". State it, or net them if
     quick.
   - The months that count, as the sheet computes them.
6. **Exports README:** add `hk.PushDatesKs` (`exports/push_dates_ks.sql`, Kosovo push sets, 113 rows), committed by the
   data session with this prompt.

**Done when:**
- The documents tell the two proofs apart.
- The without-the-build row is labelled assumed, with its facts.
- The Gjirafa50 wording is fixed and B0 is split by shop.
- E27 is in `numbers.json` and on the Evidence page, with the footnote.
- The split benefit table is in.
- No names or opinions are given as a source.
- Prompt 12's consistency test passes, `dotnet test` is green and `hours.md` is updated.
