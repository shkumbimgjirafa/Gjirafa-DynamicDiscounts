# Going live with the discount publisher

The publisher (`PricingTool.NopIntegration`, `NopDiscountWriter`) writes final prices into the shop's
NOP `Discount` tables the way the discount module does, and, for a price above the store's regular price,
the regular price itself (`TierPrice`, `NopRegularPriceWriter`: a discount can only lower a price). During the
hackathon it writes only to the sandbox (`shop_sandbox_GjirafaMall`, `shop_sandbox_GjirafaEcommerce` in
`PricingToolHackathon`), which has the exact live DDL of both (checked by the schema contract test). The cost
watcher publishes its raise to the floor the same way (since 31 Oct: before, it put a discount over the regular price,
which changes nothing on the shelf).

**Going live is a configuration change, never a code change.** The code that ran against the sandbox is
the code that runs against the shop. Nothing below has been done yet.

## Before go-live: open questions for the platform team

- **Cache:** how does a discount written outside the store app become visible? Today's monthly pushes
  are visible after `<to ask>`. Use the same mechanism: `HttpEndpoint` (URL + token) or
  `WaitForExpiry` (the shop's cache time).
- **Superseding the team's regular discounts:** the commercial owner decides whether
  `SupersedeTeamRegularDiscounts` is on, and which name patterns are "regular" (e.g. `%-disc-%`).
  Off means a cheaper team discount on a SKU wins and the SKU goes to the exceptions inbox.
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
