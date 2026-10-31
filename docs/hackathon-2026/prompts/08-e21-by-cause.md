# Prompt 08: E21 split by cause (small; after prompt 07 part A)

Prompt 07 part B left E21 as "€10.9k–16.9k a month Jun–Oct, it supports the levers' target but not a calculator trend".
A reader will take that as a €15k pricing leak the agents can fix, which it mostly isn't. The data session split it by
cause: **`docs/hackathon-2026/evidence/e21-by-cause.sql`** (reads only `PricingToolHackathon`, about 5 s; same floors,
scope and rule as `backtest-below-floor`, and its totals tie to E21 to the euro: Aug €12,476, Sep €16,936). Each line
gets the first cause that fits; the file's header defines them.

**The calculator's era, Aug–Sep 2026, normal days, EUR a month (average of the two months):**

| # | Cause | Mall KS | Gj50 KS | Both | Whose lane |
|---|---|---|---|---|---|
| 1 | SKU in a markdown (dead-stock) batch already live | 714 | 564 | 1,278 | allowed below the floor |
| 2 | GjirafaMall SKU listed on Gjirafa50 (takes Gjirafa50's price) | 4,239 | — | 4,239 | see below |
| 3 | Above the floor before an order-level voucher | 77 | 1,601 | 1,678 | marketing, not a price |
| 4 | Cross-dock, sold under its booked cost | 418 | 976 | 1,394 | lever 1 |
| 5 | No calculator price live for the SKU that day | 1,170 | 3,417 | 4,586 | lever 5's part the backtest can't score |
| 6 | Sold > 2 % under the calculator's live price | 413 | 28 | 441 | team deals; superseding is a person's call |
| 7 | At the calculator's price, under the floor on booked cost | 1,087 | 5 | 1,092 | lever 3 / cost basis |
| | **Total** | 8,117 | 6,589 | **14,706** | |

- **Cause 2 isn't only the sync policy.** €3,459 of its €4,239 is under **Gjirafa50's own** floor for the band, and
  €1,851 a month of it is under cost; only €274 sits between the two shops' floors. Mall's levers 1 and 3 already run
  on these lines (the backtest doesn't filter `IsGj50Listed`), but in v2 such a price moves on Gjirafa50 and reaches
  Mall through the sync at the next daily run (CostWatcher leaves synced Mall rows to the daily run).
- **Cause 5 is small once checked.** Against the latest local run (a proxy: August's runs weren't kept), today's engine
  **raises** SKUs worth about €1.6k a month of it (Mall €300, Gj50 €1,332). About €1.6k is on products no longer in the
  catalogue; the rest the engine keeps, cuts or holds as new products. On Gjirafa50, approvals list only changed prices,
  so a kept price lands here too.
- Before the calculator (Jun–Jul), causes 5 and 4 were €4.6–5.7k and €1.7–3.4k. Floors were edited throughout (385 band
  edits), so the yardstick moved too.

**Do, committing with `dotnet test` green:**

1. **E21 in `numbers.json`:** keep the monthly totals and add the Aug–Sep split above, with `script` =
   `evidence/e21-by-cause.sql` and the cause definitions in `method`. Replace "supports the levers' target but not a
   calculator trend" with what the split shows: about €3.0k a month is allowed or not a price (dead stock, vouchers);
   the rest sits in the lanes of levers 1, 3 and 5, partly on Gjirafa50's side of shared SKUs.
2. **Wherever E21 is quoted** (one-pager problem paragraph, `HACKATHON.md`, `backtest-results.md`, the Evidence page if it
   shows E21): use the split, not the €14.7k total, as the size of the problem the agents address.
3. **`backtest-results.md`, levers 1 and 3:** say in one line that Mall's levers include its Gjirafa50-listed lines,
   whose price v2 moves on Gjirafa50 and syncs to Mall, and how much of Mall's lever 1 and 3 gain comes from them
   (split the stored run's Mall gain by `IsGj50Listed`).
4. **Lever 5:** state cause 5 as its part the backtest can't score, with the proxy (≈ €1.6k a month static on SKUs
   today's engine raises), as context only. Don't add it to the sheet: the proxy is today's engine, not August's.

**Done when:** no document presents the €14.7k as one addressable leak, E21's script is in the repo, tests green,
`hours.md` updated.
