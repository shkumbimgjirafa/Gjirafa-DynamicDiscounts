# Prompt 04: the go-live-ready publisher, writing to the sandbox

Detail for plan section 6, row "Go-live-ready publishing". Read `CLAUDE.md` ("NOP discount writes")
and plan section 5 "Discount publishing" in full, including "Running it safely", "Sandbox" and
"Proof that it's ready".

**Data the data session loaded for you (read-only; `hk.DataSources` has the details):**
- `hk.LiveDiscountSchema`: the live columns (name, type, length, precision, scale, nullable,
  identity) of `Discount` (21 columns on GjirafaEcommerce, 22 on GjirafaMall), `Discount_AppliedToProducts`
  and the `Product` discount-flag columns, plus the discount tables' indexes, for both shop DBs.
- `hk.ActiveDiscountsKs`: 1.07M discounts on StoreId 2 live now or starting within 60 days (column
  `Db` = `GjirafaMall` / `GjirafaEcommerce`), with all their fields.
- `hk.ActiveDiscountProductsKs`: their product mappings (2.7M rows).
- `hk.ProductDiscountFlagsKs`: `HasDiscountsApplied` + `HasDiscountsAppliedStoreIds` for KS-scope
  products (2.6M rows).

**Build, committing after each step with `dotnet test` green:**

1. **`shop_sandbox` with the exact live DDL**, generated from `hk.LiveDiscountSchema` (one
   sub-schema or table prefix per shop DB, since Mall has the extra `DisplayAvailabilityTime`), same
   types, nullability, identity and indexes. Seed it from the three `hk.*` tables. Keep the seed
   to the KS demo layers; if size is a problem, seed the products the engine prices plus their
   discounts, and say what was left out.
2. **Schema contract test:** compares the sandbox DDL with a JSON snapshot of
   `hk.LiveDiscountSchema` committed in the repo; fails on any difference.
3. **`NopDiscountWriter`** in `PricingTool.NopIntegration`, behind `IPricePushService`: create,
   apply to product, set flags (merge the store id, never drop another), update in place, expire,
   rolling expiry, time-boxed campaigns, set-based chunks of ~5k, idempotent names,
   `PublishAudit` + rollback, overlap check against cheaper discounts we didn't create, and
   `IShopCacheInvalidator` (`None` in the sandbox). Only the connection string differs between
   sandbox and live; `Publisher:Target` stays `Sandbox`.
4. **Round-trip verification:** after each batch, the current-price logic reads the sandbox back and
   asserts the published price; mismatches → exceptions; above a threshold → automatic rollback.
5. **Integration tests on the sandbox:** every case in the plan's list (create, update in place,
   expire, renewal, campaign time-box, flag merge `2` + 3 → `2:3`, cheaper overlap, supersede
   on/off, idempotent re-run, rollback, unresolved SKU, failure mid-chunk).
6. **`GO-LIVE.md`** runbook and the least-privilege grant script (plan section 5), never run here.
7. **Publish one real engine run for layer 4 to the sandbox** (canary: one category, N SKUs), show
   the exact rows written, then roll it back and show the restore. Add a `TimeSheetEntry` for the
   publish (task type "Create and push the discounts for a run").

**Done when:** the contract test and all integration tests pass, a real layer-4 canary was published
to the sandbox and rolled back with matching round-trip checks, `GO-LIVE.md` exists, and
`hours.md` has the block's line.
