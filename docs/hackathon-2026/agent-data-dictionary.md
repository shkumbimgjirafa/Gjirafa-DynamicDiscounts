# Data dictionary for Alpha and Nova

This text goes into the agents' system prompt (prompt-cached). It describes the `agent.*` views
they read through `query_data`, and the traps that make an obvious query wrong.

## The business in one paragraph

Two Kosovo shops, priced in EUR. **Layer 1 = GjirafaMall KS** (general merchandise; only the
company's own vendors are in scope) and **layer 4 = Gjirafa50 KS** (electronics). A shared SKU
(a "Gj50-listed" product on Mall) takes Gjirafa50's price on Mall. Prices are set per **price band**;
each band has a **margin floor** (never sell below it, except dead-stock clearance) and a **target
margin** (the blended goal). Outlet products and the Apple and Starlink brands are outside the
pricing tool and already removed from every view.

## Views

| View | One row per | Key columns |
|---|---|---|
| `agent.Layers` | layer | `LayerId`, `LayerName`, `Currency` |
| `agent.Products` | product per layer | `Sku`, `Name`, `Brand`, `CategoryL1`, `CategoryL2`, `MainGroup`, `IsGj50Listed` |
| `agent.SalesLines` | order line, Oct 2024 → today | `SaleDate`, `Sku`, `Qty`, `NetRevenue`, `GrossRevenue`, `Cogs`, `GrossProfit`, `DiscountGiven`, `ListValue`, `StockClass`, `UnitCostInclVat`, `BandId`, `Brand`, `CategoryL1`, `MainGroup`, `IsGj50Listed` |
| `agent.DailySales` | layer × day × main group × L1 × band | `Lines`, `Units`, `NetRevenue`, `GrossProfit`, `MarginPct`, `DiscountGiven` |
| `agent.CampaignHistory` | campaign window | `Name`, `StartDate`, `EndDate`, `CampaignType` (main/mini), `Orders`, `EffectiveMarginPct`, `NormalMarginPct`, `ProfitPerDayVsNormal`, `Label` |
| `agent.CostChanges` | SKU with sales in both windows | `UnitCostPrior60`, `UnitCostLast30`, `ChangePct` |
| `agent.BandConfig` | price band | `BandId`, `BandName`, `MinPrice`, `MaxPrice` (cost limits), `MarginFloorPct`, `TargetMarginPct` |
| `agent.BandTargetHistory` | band × engine run, from 2026-10-21 | the floors and targets each run used |
| `agent.TestPhaseBatches` | batch of calculated prices that went live by hand | `ApprovedUtc`, `SkuCount`, `Profit30Before`, `Profit30After` |
| `agent.TestPhasePriceChanges` | SKU in such a batch | `OldPrice`, `NewPrice`, `ChangePct` |

## Definitions

- **Money columns in `agent.SalesLines` are line totals** (they already include `Qty`). Unit price =
  `SUM(NetRevenue) / SUM(Qty)`. Never multiply by `Qty` again.
- **`NetRevenue` excludes VAT; `GrossRevenue` includes it.** `GrossProfit = NetRevenue − Cogs`, where
  `Cogs` is the cost **booked at the time of sale**. Margin % = `SUM(GrossProfit) / SUM(NetRevenue)`;
  always sum before dividing, never average line margins.
- **Bands are by cost, not price:** a SKU's band is the one whose `[MinPrice, MaxPrice)` contains its
  cost including VAT. `BandId` on a sales line uses the booked unit cost × VAT and today's limits.
- **Floors and targets are on the engine's basis**: (price − cost incl. VAT) / price, on the
  VAT-inclusive price. That is close to the booked margin above but not identical line by line;
  say which one you used.
- **`StockClass`:** `InStock` = the unit was in a Kosovo warehouse before the order; `CrossDock` =
  it arrived after the order (bought to order, cost risk); `NoWmsUnit` = never passed a KS warehouse.
- **`DiscountGiven` / `ListValue`:** the discount on the line and the pre-discount value. `DiscountGiven` includes VAT,
  `ListValue` doesn't (`ListValue` = (`GrossRevenue` + `DiscountGiven`) / VAT, checked on sampled lines 2026-10-30), so
  discount depth = `SUM(DiscountGiven) / SUM(GrossRevenue + DiscountGiven)`. Leave out lines claiming more than 80 % off:
  968 such lines (0.2 %) carry 13 % of all discount and are data errors.

## Traps

- **The newest two weeks look better than they are:** cancellations post late and remove lines.
  Compare like with like, or stop the window two weeks back.
- **Campaign days distort everything** (units 2–5×, margin much lower). Check `agent.CampaignHistory`
  and either exclude campaign days or compare campaign with campaign.
- **Seasonality:** compare with the same weeks of 2025, not just the weeks before.
- **Gj50-listed products are about a third of Mall revenue at a thinner margin**, and their Mall price
  follows Gjirafa50. Any Mall margin figure must say how it treats them.
- **Long tail:** most SKUs sell one unit in two months. Decide at category or band level; per-SKU
  numbers with fewer than ~5 units are noise.
- **GjirafaMall stopped booking discounts on the line in Nov 2025:** from then only about 5 % of layer-1 lines carry
  `DiscountGiven` (80–90 % before; Gjirafa50 unchanged). Discount depth on GjirafaMall isn't comparable across that break.
- **`CategoryL1 = 'Unknown'`** is real volume (products mapped only to promo pages); don't drop it silently.
- `agent.CostChanges` is a proxy from booked costs of what sold; it misses SKUs that didn't sell.
- Queries: SELECT only, one statement, at most 500 rows back, 30 seconds. Aggregate in SQL; don't
  pull raw lines to count them yourself.
