# Going live with the discount publisher

The publisher (`PricingTool.NopIntegration`, `NopDiscountWriter`) writes final prices into the shop's
NOP `Discount` tables the way the discount module does. During the hackathon it writes only to the
sandbox (`shop_sandbox_GjirafaMall`, `shop_sandbox_GjirafaEcommerce` in `PricingToolHackathon`), which
has the exact live DDL (checked by the schema contract test).

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

## Runbook

1. **Least-privilege login per shop DB.** A DBA runs
   `PricingTool.NopIntegration/GoLive/grant-publisher-login.sql` on GjirafaMall and GjirafaEcommerce:
   INSERT/UPDATE on `Discount`, INSERT on `Discount_AppliedToProducts`, UPDATE on
   `Product(HasDiscountsApplied, HasDiscountsAppliedStoreIds)` only, SELECT for reading back, DELETE
   denied everywhere.
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
   the batch changed from `ai.PublishAudit` (old amounts, dates and product flags) and expires what it
   created; nothing is deleted.

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
