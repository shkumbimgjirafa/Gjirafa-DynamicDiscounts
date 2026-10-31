# Prompt 07: raises reach the shelf (regular prices on the sandbox), and E20 recomputed in this repo

Detail for the two problems AI-judge dry run 2 still holds against us (`ai-judge/dry-run-2-juror-report.md`):
- **"The money levers need price raises that the built publisher cannot make"** (its biggest unproven assumption;
  Works can't reach 4 while it stands). Levers 1, 3 and 5 are about half of framing A and earn by raising prices.
- **"E20 and E21 were measured in the main repo, with no script here"** (high). E20 carries all of framing B and
  lever 4's mid and high cases.

Read `CLAUDE.md`, plan section 5 (publisher) and `GO-LIVE.md` first. The data session has loaded what this needs
(rows in `hk.DataSources` #34–37); nothing below needs the replica.

## Part A: the regular-price publisher (sandbox only)

**What the data shows** (`hk.LivePriceSchema`, `hk.TierPriceConventions`, `hk.TierPriceMarginProbe`, all loaded
31 Oct from the replica, metadata and aggregates only):
- The shelf price per store is **`TierPrice`** (`ProductId`, `StoreId`, `Price`, `OldPrice`), the newest row per
  product and store; that's what the engine reads (`SourceQueries.cs`, `#tier`). Writing it is how a raise reaches the
  shelf. `Product.Price` is **not** the store's price (it equals the KS tier price on only 1–4 % of products): don't
  touch it.
- **One row per product and store, updated in place**: 2,437,703 Mall KS products have 2,437,704 rows; 472,443 Gj50 KS
  products have 472,444. No timestamps, no triggers on `TierPrice` or `Product`.
- On the newest rows: `CustomerRoleId` NULL, `Quantity` 0, `StartDateTimeUtc` / `EndDateTimeUtc` NULL; prices have at
  most 2 decimals. `Product.HasTierPrices` = 1 wherever a tier row exists.
- **`ProfitMargin` is a legacy value**, not a margin: 1.00–1.30 on Mall, mixed on Gj50, and it matches no price/cost
  formula on 20,000 sampled KS rows. Leave it as it is; NULL on a row we insert.
- `OldPrice` (the strike-through): 0 on 23 % of Mall KS rows / 15 % of Gj50 KS, equal to `Price` on 62 % / 34 %, above
  it on 20 % / 52 %.
- Types differ by shop: `TierPrice.Price` / `OldPrice` are `decimal(18,4)` on GjirafaMall, `decimal(18,2)` on
  GjirafaEcommerce.
- **`hk.TierPricesKs`**: today's newest KS tier row per product for both shops (2,910,146 rows), to seed the sandbox.

**Build, committing after each step with `dotnet test` green:**

1. **Sandbox `TierPrice`** in `shop_sandbox_GjirafaMall` / `shop_sandbox_GjirafaEcommerce` with the exact live DDL from
   `hk.LivePriceSchema` (PK, the two indexes, each shop's decimal types), seeded from `hk.TierPricesKs`; add
   `Price`/`HasTierPrices` to the sandbox `Product` if the writer needs them. Extend the schema contract test the way
   it covers the discount tables.
2. **`NopRegularPriceWriter`** next to `NopDiscountWriter`, behind the same switches (kill switch, enabled layers,
   canary, `Publisher:Target` = Sandbox, the `shop_sandbox_` schema guard). One rule decides which writer a price goes
   to, so a SKU is never written twice:
   - **a raise** (target price above the current tier `Price`): update the newest tier row in place (`Price`; never
     `ProfitMargin`). If our own `ai-price-<layer>-<sku>` discount exists, expire it in the same transaction
     (`EndDateUtc` = now), or it keeps the old price on the shelf. If a **team** discount is cheaper, the shopper keeps
     paying it: write the tier price anyway, count it as "raised under a team deal", and list it in the exceptions
     inbox (superseding team deals stays a person's call, as now);
   - **a cut**: the existing `ai-price` discount path, unchanged;
   - **`OldPrice`**: unchanged, except that when the new `Price` would be above a non-zero `OldPrice`, set `OldPrice` =
     the new `Price` (no strike-through on a raise; never invent a reference price). Say so in `GO-LIVE.md` as a
     convention to confirm with the team;
   - **no tier row** for the store: insert one (`CustomerRoleId` NULL, `Quantity` 0, no dates, `ProfitMargin` NULL)
     and set `Product.HasTierPrices` = 1;
   - **concurrency**: in the transaction, re-read the row; if its `Price` isn't the shelf price the run saw, skip the
     SKU and report it ("changed since the run"), never overwrite someone else's change.
   Every write stores the previous row in `PublishAudit` first; rollback restores `Price` / `OldPrice` exactly and
   deletes only a row this writer inserted.
3. **Run week and the daily cycle publish raises too** (to the sandbox): the summary shows discounts created /
   updated / expired, tier prices raised, raised under a team deal, skipped as changed. The checked push file stays
   the live path until go-live, unchanged.
4. **Tests:** a round trip per shop (raise → read back the shelf price the engine's own `#tier` logic would compute
   → rollback → byte-identical row); our own discount expired on a raise; a team discount left alone; an inserted row
   rolled back; the concurrency skip; the decimal(18,2) shop; the kill switch stops both writers.
5. **One real Run week per KS layer** with both writers, recorded in `hours.md`: how many of the run's prices now
   reach the sandbox shelf, against Run week #1's 11,917 of 37,926 (68 % didn't). Then update the one-pager,
   `HACKATHON.md` and the AI-judge finding's row: raises are built and tested on the sandbox; still not live, and
   cheaper team deals still win until a person supersedes them.
6. **`GO-LIVE.md`:** the write grant for `TierPrice` (UPDATE `Price`, `OldPrice`; INSERT) and `Product.HasTierPrices`,
   added to the least-privilege script; and two questions to settle with the team before the first live write: how the
   shop's price cache picks up a direct `TierPrice` write (the same question as for discounts), and the `OldPrice`
   convention above.

**Done when:** both KS layers ran Run week with raises written and read back in the sandbox, rollback restores
exactly, tests green, the documents say what's still not live.

## Part B: E20 from this repo's data

The data session ported the main repo's measurement to SQL that reads only `PricingToolHackathon`:
**`docs/hackathon-2026/evidence/e20-test-phase.sql`** (two result sets; about 8 s). On 31 Oct it gives:

| | Mall KS | Gj50 KS | Both | Measured 27 Oct (main repo) |
|---|---|---|---|---|
| Before/after, Aug | €8,551 | €2,517 | €11,068 | €11,184 |
| Before/after, Sep | €7,461 | €2,288 | €9,749 | €10,079 |
| Before/after, Aug–Sep average (**E20 mid**) | | | **€10,409** | €10,632 |
| Same-weeks control, Mall run of 3 Sep (#25), changed vs kept / vs not in the approval | €3,263 / €4,088 a month | | | €3.5–4.3k |
| Same-weeks control, Gj50 run of 21 Jul (#1), vs not in the approval (it changed every product it held) | | €1,139 a month | | not measured |

The small drops are late cancellations removing lines since 27 Oct. **New:** a controlled point on Gjirafa50 too, so
the low case is no longer "one run on one layer". Both runs were live through September.

1. **Wire it into `build-numbers.ps1`:** E20's `script` = this file; mid from result set 1; **low = Mall #25 vs kept
   (€3,263) + Gj50 #1 vs not in the approval (€1,139) = €4,402**, labelled "measured, same-weeks control, one run per
   layer, the stricter control on Mall", with €4.1k + €1.1k (the other Mall control) stated next to it. High stays
   €13,000, labelled as quoted from the main repo (the 2025 season adjustment isn't recomputed here).
2. **Follow the numbers through:** B0 per scenario in the financial sheet, lever 4 (E4) at its controlled rate
   (€4,402 / Kosovo's GP, instead of €4.0k), the one-pager's E20 sentence (drop "a floor" for "one controlled run per
   layer"), `HACKATHON.md`'s known-limitations line, and dry run 2's finding rows 2 and 3.
3. **E21 the same way, if quick:** below-floor GP forgone per month, Jun–Oct 2026, on `hk.SalesLines` with the
   hand-set floor of each day (the backtest's lever 2 already rebuilds it from the audit log), booked COGS, campaign
   days marked. Label the scope difference from the Jun/Jul analysis (tool scope here; Apple/Starlink out). The
   monthly trend shows the calculator's floors taking effect and what still leaks below them, the leak v2's levers target.
4. **The backtest output the juror couldn't find:** commit the stored run as JSON (`evidence/backtest-snapshot.json`;
   CSVs stay ignored), check it holds only SKUs and money, and correct `hours.md`'s "committed as CSV".

**Done when:** `numbers.json` has no "script: not in this repo" for E20 (and E21 if done), the sheet and one-pager
carry the recomputed values, tests green, `hours.md` updated.

## Part C: small items from the handoff that need no person

- Restart the engine host so it runs the current build (it still ran `e47f481`'s, without the per-SKU 3 % floor).
- Keep the database under the cap: `hk.TierPricesKs` and the sandbox `TierPrice` add about 0.3 GB (5.3 GB used on
  31 Oct).
