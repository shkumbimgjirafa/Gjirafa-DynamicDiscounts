# Going live with the discount publisher

The publisher (`PricingTool.NopIntegration`, `NopDiscountWriter`) writes final prices into the shop's
NOP `Discount` tables the way the discount module does, and, for a price above the store's regular price,
the regular price itself (`TierPrice`, `NopRegularPriceWriter`: a discount can only lower a price). During the
hackathon it writes only to the sandbox (`shop_sandbox_GjirafaMall`, `shop_sandbox_GjirafaEcommerce` in
`PricingToolHackathon`), which has the exact live DDL of both (checked by the schema contract test). The cost
watcher publishes its raise to the floor the same way (since 31 Oct: before, it put a discount over the regular price,
which changes nothing on the shelf).

**Going live is a configuration change, never a code change.** The code that ran against the sandbox is
the code that runs against the shop. Nothing below has been done yet. The sandbox mirror (the daily `mirror-L<layer>`
step that copies the live shop's other discounts and regular prices into the sandbox) exists only for the sandbox: with
`Publisher:Target` = `Live` it does nothing.

## Before go-live: open questions for the platform team

- **Cache:** how does a discount written outside the store app become visible? Today's monthly pushes
  are visible after `<to ask>`. Use the same mechanism: `HttpEndpoint` (URL + token) or
  `WaitForExpiry` (the shop's cache time).
- **Superseding the calculator's own earlier pushes:** the commercial owner decides whether
  `SupersedeTeamRegularDiscounts` is on. On, a regular publish ends a cheaper discount only when it is the calculator's own
  earlier push imported by the team: a single-product discount carrying a price the tool approved for that SKU. A team deal
  (`techtober-2026`, a warehouse deal, a campaign) is never ended. Off (the default), such a SKU goes to the exceptions
  inbox, as "own-push" or "cheaper-deal" (a SKU with a team deal under the price is "cheaper-deal" even when its cheapest
  deal is an own push: the switch can't clear it). On 31 Oct, 96.5 % of the held prices were the calculator's own October
  pushes; with the switch on, 98.3 % of both KS layers' checked prices reached the sandbox shelf (171,108 of 174,087,
  measured on runs #1109 and #1107, then rolled back), and team deals held 2,969.
  **Decided 31 Oct by the user: on** (Engine, Web and AgentTools configs). Republished with the margin-trim cuts held back:
  Gjirafa50 #1109 25,390 of 26,604 checked prices on the sandbox shelf (batch #10162), GjirafaMall #1107 49,214 of 50,969
  (#10164); team deals still hold 1,214 and 1,754, never ended by this switch.
  **1 Nov, the user:** the trim cuts go out again (`HoldBackTrimCuts` off, same three configs), and a price the run checker
  blocks is held alone while the rest of the run goes out (only a check that didn't finish stops the whole run).
- **Team deals under the floor after a cost rise (the user's decision, 31 Oct):** the cost watcher ends a cheaper team
  discount that keeps a price under its floor (`CostWatcher:EndTeamDeals`, on), when the discount covers only that product
  and no chat rule `protect-discount` matches its name; then the floor price applies. Audited like any supersede and
  rolled back with its batch; the sandbox mirror leaves a deal the tool ended as ended. This reaches the live shop with
  the publisher, so it is part of the go-live decision: turn `EndTeamDeals` off to go back to holding them for a person.
  Regular publishes (Run week, the daily cycle) are unchanged: they still leave team deals to the switch above.
- **App-only deals and the engine:** a cheaper app-only discount (`DiscountPlatformTypeId` 2) no longer stops a regular
  publish (the web price is ours; the app shopper keeps the cheaper). The engine (the imported calculator) still takes an
  app-only deal as a SKU's current price. Is that what the team wants it to read?
- **Index on GjirafaEcommerce:** its `Discount` has only `PK_Discount` (GjirafaMall also has two
  covering indexes). The publisher reads through `Discount_AppliedToProducts` (indexed) and never
  scans `Discount` by name, so none is needed; confirm with the DBA.
- **Cache, for regular prices too:** how does a `TierPrice` written outside the store app reach the shop's
  price cache? The same question as for discounts; the answer may differ (product price cache vs discount cache).
- **The `OldPrice` convention on a raise:** the publisher leaves `OldPrice` (the strike-through) as it is, except
  that a new price above a non-zero `OldPrice` lifts it to the new price, so a raise never shows a strike-through and
  no reference price is invented; an `OldPrice` of 0 stays 0. Confirm with the team that this is how they set it.
- **`TierPrice.ProfitMargin`** is a legacy value (1.00–1.30 on GjirafaMall, mixed on GjirafaEcommerce; it matches no
  price/cost formula on 20,000 sampled KS rows). The publisher never writes it, and leaves it NULL on a row it inserts.
  Confirm nothing reads it.

## Runbook

1. **Least-privilege login per shop DB.** A DBA runs
   `PricingTool.NopIntegration/GoLive/grant-publisher-login.sql` on GjirafaMall and GjirafaEcommerce:
   INSERT/UPDATE on `Discount`, INSERT on `Discount_AppliedToProducts`, UPDATE on
   `Product(HasDiscountsApplied, HasDiscountsAppliedStoreIds, HasTierPrices)` only, UPDATE on
   `TierPrice(Price, OldPrice)` and INSERT on `TierPrice`, SELECT for reading back. DELETE is denied on the discount
   tables and `Product`; on `TierPrice` it is granted only so a rollback can remove a row the publisher inserted
   (drop that grant and such a rollback fails, leaving the row for a person).
2. **Cache invalidation.** Set `Publisher:CacheInvalidation` to `HttpEndpoint` (with
   `Publisher:CacheEndpointUrl`, and `Publisher:CacheEndpointToken` as a secret) or `WaitForExpiry`
   (with `Publisher:CacheWaitSeconds`).
3. **Write connection as a secret.** `Publisher:Shops:GjirafaMall:ConnectionString` and
   `Publisher:Shops:GjirafaEcommerce:ConnectionString` (the login from step 1) in the secret store;
   `Publisher:Shops:<Db>:Schema` = `dbo`.
4. **One layer, canary first.** `Publisher:Target` = `Live`. Publish one category, a few dozen SKUs:
   `PricingTool.AgentTools publish-canary <layer> --run <engine run> --category <category> --max 30`.
5. **Check the round-trip is green, then widen.** Every batch reads its prices back; mismatches go to
   `ai.PublishException`, and above `Publisher:RoundTripRollbackPct` (5 %) the batch rolls itself back.
   Widen to the layer, then to the next layer.
6. **Rollback is one command.** `PricingTool.AgentTools publish-rollback <batch id>` restores every row
   the batch changed from `ai.PublishAudit` (old amounts, dates, product flags, regular prices and their
   `OldPrice`) and expires the discounts it created; the only delete is a `TierPrice` row the batch itself inserted.
   It works with the kill switch pulled. If a publish was interrupted (the process or a database stopped mid-batch), the
   batch stays `Running` and rollback refuses it: `PricingTool.AgentTools publish-reconcile <batch id>` first marks every
   audited change the shop shows as written (a chunk can commit before its audit rows are marked applied), then
   `publish-rollback` restores it exactly. Found on 31 Oct when LocalDB stopped during a GjirafaMall publish; rehearsed on
   that batch (#99): 206 of 206 regular prices back to the seed values, 17,066 discounts ended, 642 product flags restored.

**Stop at any time:** set `Publisher:Target` back to `Sandbox`, or turn `Agent:Enabled` off (the kill
switch). Our `ai-price` discounts expire on their own within `Publisher:RollingExpiryDays` (7) if the
system stops renewing them, and the shelf price applies again.

## What the publisher writes (for the reviewer)

| Field | Value |
|---|---|
| `Name` | `ai-price-<layer>-<sku>` (one per SKU and store, updated in place) or `ai-campaign-<key>-<sku>` |
| `DiscountTypeId` / `UsePercentage` / `DiscountPercentage` | 2 / 0 / 0 |
| `DiscountAmount` | the final price (the price, not an amount off) |
| `DiscountApplyTypeId` / `DiscountPlatformTypeId` | 2 (public) / 0 (web + app) or 2 (app only) |
| `RequiresCouponCode`, `IsCumulative`, `DiscountLimitationId`, `LimitationTimes`, `AppliedToSubCategories`, `IsVendorDiscount` | 0 |
| `DisplayAvailabilityTime` | 0 (GjirafaMall only) |
| `StartDateUtc` / `EndDateUtc` | now / now + 7 days, renewed each publish (campaigns: their window) |
| `StoreId` | KS 2, MK 1, AL 3 |
| `AdminComment` | `pricing-tool batch <id>; run <engine run>` |
| `Product.HasDiscountsApplied` / `HasDiscountsAppliedStoreIds` | 1 / the store merged into the sorted list, never dropping another |

Regular prices (`TierPrice`, the newest row of the product and store, which is what the engine reads):

| Field | Value |
|---|---|
| `Price` | the new price, updated in place, only while the row still holds the values read before the write |
| `OldPrice` | unchanged, except a new price above a non-zero `OldPrice` lifts it to the new price |
| `ProfitMargin`, `CustomerRoleId`, `Quantity`, dates | never touched; on an inserted row: NULL, NULL, 0, NULL |
| `Product.HasTierPrices` | 1 when the publisher inserts the store's first row |
| our `ai-price-<layer>-<sku>` discount | ended (`EndDateUtc` = now) in the same transaction, or it would keep the old price on the shelf |
| a cheaper team deal | left as it is: the shopper keeps paying it ("raised under a team deal", in the exceptions inbox) |
| the calculator's own earlier push (a cheaper single-product discount carrying a price the tool approved) | left as it is ("raised under our own push"), or ended in the same transaction with `SupersedeTeamRegularDiscounts` on |
| a cheaper app-only discount | left as it is: the app shopper keeps it; the web price is ours (noted, "app-deal-cheaper") |
