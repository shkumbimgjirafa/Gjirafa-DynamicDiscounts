# Data prep for the hackathon DB

Data-prep scripts, not solution code. Each one reads a source **read-only** and loads LocalDB
`PricingToolHackathon`; every load writes a row to `hk.DataSources` (table, source, origin, time,
row count). Connection strings are never stored here: the replica's comes from the hackathon
user-secrets store, the tool DB's is passed in at run time.

| Table(s) | Source | Script | Scope |
|---|---|---|---|
| `PricingTool.*` (21 tables) | Deployed pricing tool DB | `Copy-ToolDbKs.ps1` | All config; per-SKU and history tables for KS layers 1 and 4. Who-columns → "team member" |
| `hk.SalesLines` | NOP replica (Sales Report lines + WMS) | `sales_lines_ks.sql` | KS layers 1 and 4 from 2024-10-01; tool scope; booked COGS; `StockClass` (InStock / CrossDock / NoWmsUnit); no order or customer ids |
| `hk.Products` | NOP replica | `products_ks.sql` | KS products: brand, category L1/L2 (deepest published mapping), Mall main group, outlet / Apple-Starlink / Gj50-listed flags |
| `hk.CampaignWindows` | Analysis of 2026-10-07 | inline in the data session | 16 KS campaigns Oct 2024–Oct 2026 + Black Friday 2026 calendar rows |
| `hk.LiveDiscountSchema` | NOP replica, metadata only | `live_discount_schema.sql` | Columns and indexes of `Discount`, `Discount_AppliedToProducts`, Product discount flags; both shop DBs |
| `hk.ActiveDiscountsKs`, `hk.ActiveDiscountProductsKs` | NOP replica | `active_discounts_ks.sql`, `active_discount_products_ks.sql` | StoreId 2 discounts live now or starting within 60 days, with their product mappings |
| `hk.ProductDiscountFlagsKs` | NOP replica | `product_discount_flags_ks.sql` | `HasDiscountsApplied` + `HasDiscountsAppliedStoreIds` for KS-scope products |
| `agent.*` views | — | `agent_views.sql` | The curated views behind `query_data` |
| `hk.SalesLinesMkAl` | NOP replica (Sales Report lines) | `sales_lines_mkal.sql` | MK/AL layers 2, 3, 5, 6 from 2026-07-01 for backtest lever 4; same scope rules as KS; money in local currency (MKD/ALL) plus `*Eur` columns at the shop's `Currency.Rate` on the extraction day (`EurRate` per row); no `StockClass`. Kept apart from `hk.SalesLines`, which is EUR-only and reloaded daily |
| `hk.PushDatesMkAl` | NOP replica | `push_dates_mkal.sql` | Discount sets of 200+ rows on the MK/AL stores (StoreId 1 = MK, 3 = AL, both shop DBs) from 2026-06-15, by name prefix, apply type, platform type (0 = all, 2 = app only) and start day |
| `hk.LivePriceSchema` | NOP replica, metadata only | `live_price_schema.sql` | Columns, indexes and triggers of `TierPrice` (the shelf price per store) and the price columns of `Product`; both shop DBs |
| `hk.TierPriceConventions`, `hk.TierPriceMarginProbe` | NOP replica, aggregates only | `tier_price_conventions.sql`, `tier_price_margin_probe.sql` | How tier rows are used per shop and store (one row per product, updated in place); what `ProfitMargin` holds (a legacy value, not a margin) |
| `hk.TierPricesKs` | NOP replica | `tier_prices_ks.sql` | Today's newest KS (StoreId 2) tier row per product, both shops: seeds the regular-price publisher's sandbox |

Generic runner: `Copy-ReplicaQuery.ps1 -SqlFile … -DestTable … -CreateSql …`.

## Sanity check (plan section 4), Fri 30 Oct

Gross profit of each approval's SKUs in the 30 days before it, computed from `hk.SalesLines`
(same layer), against the tool's own `Approvals.Profit30Before`:

| Approval | Layer | Approved | Tool's Profit30Before | From hk.SalesLines |
|---|---|---|---|---|
| 2 | 1 | 2026-07-21 | €11,751 | €11,710 |
| 12 | 1 | 2026-08-06 | €50,667 | €50,672 |
| 25 | 1 | 2026-09-03 | €69,750 | €69,763 |
| 30 | 1 | 2026-10-06 | €75,450 | €75,450 |
| 35 | 1 | 2026-10-19 | €83,076 | €83,076 |
| 1 | 4 | 2026-07-21 | €3,158 | €3,158 |
| 32 | 4 | 2026-10-07 | €20,471 | €20,471 |
| 37 | 4 | 2026-10-21 | €50,047 | €51,069 (+2%, recent window; late cancellations still posting) |

All within a few percent, most to the euro: the exported lines are right.
