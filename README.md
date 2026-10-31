# Gjirafa Dynamic Pricing Tool: pricing calculator v0.5 + Pricing Tool v2 (hackathon build)

The calculator described below (v0.5) works with rules and statistics; it has no AI model in it.
The AI part of Pricing Tool v2, the pricing agent Alpha and the campaign agent Nova, is the Gjirafa
AI Hackathon 2026 build in `PricingTool.Agent`. See `DECLARATION.md` and
`docs/hackathon-2026/kickoff-plan.md`.

In-house automated dynamic pricing for Gjirafa's e-commerce stores. Recalculates **proposed**
prices on a configurable schedule (default daily 03:00 UTC) from margin, demand, inventory and
historical sales — profit-first: hold sales volume, lift margin 25%+.

**The engine never writes to live platform prices.** It writes proposals to its own database.
Pushing approved prices to the platform is a separate, explicit, Manager-triggered step.

## Hackathon build

This repository is the Gjirafa AI Hackathon 2026 build (Track A): two AI agents and a NOP discount publisher on top of the
calculator below. What's new, how to run it in five steps, where the AI's output becomes a decision, what is mocked or
unfinished, and how every figure reproduces: [`HACKATHON.md`](HACKATHON.md). The app's tabs are Run, Chat, Campaigns and
Proposals; the evidence is a file, [`docs/hackathon-2026/evidence/evidence.html`](docs/hackathon-2026/evidence/evidence.html).

The rest of this README is the calculator's own, as imported. Three files it links to belong to the main repo and
were not copied here: `scripts/usp_GetDailyPricingDataset.sql`, `docs/algorithms/cross-dock.md` and
`k8s/web-deployment.yaml` (this repo has no `k8s/`, `.env` or pipeline by rule).

## Layers (multi-store)

The tool is **multi-layer**: a *layer* is a Brand × Country combination, and everything — the
source pull, snapshots, proposals, price bands, schedule and dashboard — is scoped to the
selected layer. Six layers ship seeded and active:

| Brand | Country | Store DB | StoreId | TranslationCountryId | Warehouse | Currency | Vendor filter |
|---|---|---|---|---|---|---|---|
| GjirafaMall | KS | `GjirafaMall` | 2 | 1 | 2 | EUR | Gjirafa/Dino Toys/Mysu/Apple/YZATT |
| GjirafaMall | MK | `GjirafaMall` | 1 | 3 | 1 | MKD | (same set) |
| GjirafaMall | AL | `GjirafaMall` | 3 | 2 | 3 | ALL | (same set) |
| Gjirafa50 | KS | `GjirafaEcommerce` | 2 | 1 | 2 | EUR | **all vendors** |
| Gjirafa50 | MK | `GjirafaEcommerce` | 1 | 3 | 1 | MKD | **all vendors** |
| Gjirafa50 | AL | `GjirafaEcommerce` | 3 | 2 | 3 | ALL | **all vendors** |

- The source query reads the operational data (vendors, products, stock, orders, tier prices,
  discounts) from the layer's **operational database** (`GjirafaMall` ↔ `GjirafaEcommerce`,
  substituted into the query per layer) and cost/margin from the **shared** `GjirafaTranslations`
  database keyed by `TranslationCountryId`.
- Each layer carries its own price bands, per-algorithm weights and schedule. Switch the active
  layer from the brand → country dropdown in the nav; it's remembered in your session.
- Runs are **per-layer but serialized globally** (one run at a time across all layers — the
  bulk-write path is not concurrency-safe). The scheduler fires each active layer on its own slot.
- **Excluded from every layer:** soft-deleted products (`Product.Deleted = 1`, removed from the
  storefront), outlet products (`Product.IsOutlet = 1`, priced by a separate
  system) **plus outlets that flag misses** — the storefront also tags them in the name, so any
  product whose `Name` contains the opening tag `[OUTLET` is excluded too (matched with `CHARINDEX`,
  since `[` is a `LIKE` wildcard). Deliberately the *tag*, not a bare "outlet": that word appears in
  ~60 legitimate product names (power strips, PDUs, drain hoses — "Prizë shtesë Equip 3 Outlet French
  PDU"), which must stay priced. Also excluded: products not published in the layer's store (`UnpublishedStoreids`), and **Apple- and
  Starlink-branded products** — anything mapped (via `Product_Manufacturer_Mapping`) to a manufacturer
  whose name starts with `Apple` (`Apple`, `APPLE`, `Apple Authorized Reseller`, `Apple Refurbished`)
  or `Starlink`. The brand-level rule is needed because these brands also sell under Gjirafa vendors,
  so the vendor filter alone cannot catch them.

The `Layer` rows (IDs, currency, toggles, schedule) are seeded by `DbSeeder` and the `AddLayers`
migration; activating/deactivating or retuning a layer is a data edit (no redeploy).

### Cross-brand price sync (GjirafaMall adopts Gjirafa50)

GjirafaMall and Gjirafa50 share a warehouse, and products sold under both brands must carry the
**same** shelf price. So for any SKU the **same-country Gjirafa50** layer has already priced (its
latest successful run), the GjirafaMall run **adopts that price verbatim** — reason `SYNCED_TO_GJ50`.
The adopted price wins over Mall's own algorithms, guardrails, rounding, and even a would-be
skip / new-product hold (Mall's cost/anchor/dead-stock stage are still stamped for auditability).
Gj50 proposals a Manager **Rejected** are *not* adopted (Mall prices that SKU normally instead), and
skipped Gj50 rows are ignored. This is why each **Gjirafa50 layer is scheduled 30 minutes ahead** of
its same-country GjirafaMall layer (`DbSeeder.Gj50RunLeadMinutes`) — Gj50's prices must exist before
Mall runs. Non-Mall layers and single-brand countries sync nothing. Logic lives in
`PricingRunOrchestrator.LoadSyncedGj50PricesAsync` / `SyncedProposal`.

## Solution layout

| Project | Purpose |
|---|---|
| `PricingTool.Core` | Domain: the 4 pricing algorithms, weighted scoring, guardrails (incl. new-product hold), VAT math, psychological rounding, demo data generator |
| `PricingTool.Data` | EF Core (migrations, `PricingTool` schema), per-layer source dataset readers, run orchestrator, bulk writer, CSV push integration, audit |
| `PricingTool.Engine` | Background worker — scheduled recalculation, looping active layers (schedule read per layer from DB, admin-editable live) |
| `PricingTool.Web` | ASP.NET Core admin UI + impact dashboard, layer switcher (dev-shim auth, Analyst/Manager roles) |
| `PricingTool.Tests` | xUnit suite (380 tests): every algorithm incl. 0-vs-NULL handling, guardrails (margin floor, age-tiered dead-stock tunnel, margin-target), dead-stock markdown pacing, cross-brand price sync, gross-margin & VAT math, profit/margin KPIs, rounding-never-violates-guardrails (all conventions), weighted scoring, orchestrator policies |

## Quick start (demo mode — no source database needed)

Demo mode is **on by default** (`PricingEngine:UseDemoData = true`). It fabricates a realistic
catalog (~360 SKUs across all bands: fast movers, dead stock, discount-insensitive, missing-cost…)
**for every active layer** and backfills 35 days of snapshot history on first boot so each layer's
dashboard has trends.

Requirements: .NET 8 SDK, SQL Server (LocalDB is fine — default connection strings use it).

```bash
dotnet run --project PricingTool.Web
```

1. Open the printed URL — the app loads straight to the dashboard for the default layer
   (**GjirafaMall — Kosovo**). Locally, Porta SSO is off by default, so a dev no-auth shim signs you
   in with both roles and every page is accessible (no login). In production, Porta SSO gates access
   against the Users allowlist. See [Authentication](#authentication).
2. Use the **layer switcher** (top-right, brand → country) to scope the whole app to another layer.
3. **Schedule → Run now** executes a full pricing cycle for the current layer (pull → snapshot → propose).
4. **Proposals** — review, filter, approve (changes >±20% require explicit confirmation), then
   **Push** writes the approved-prices CSV to `exports/` (the v1 integration point).
5. **Dashboard** — margin/revenue/volume trends vs baseline, algorithm/band attribution, health flags.

The Engine worker (`dotnet run --project PricingTool.Engine`) is only needed for *scheduled*
runs; on-demand runs work from the Web app alone. Both can run side by side — a DB-level guard
serializes runs.

CLI escape hatch: `dotnet run --project PricingTool.Engine -- --run-now [--layer <code>]` executes
one full pricing run immediately (applying migrations/seeding first) and exits. With no `--layer`
it runs **all active layers** sequentially; `--layer` accepts `MK`, `GjirafaMall/MK`, or the
display name. Handy for ops, cron, and smoke tests.

## Production setup

### 1. Databases & connection strings

Two connection strings (in `appsettings.json` or environment variables
`ConnectionStrings__PricingToolDb` / `ConnectionStrings__SourceReadOnly`):

- **`PricingToolDb`** — the tool's own database (read/write). All tables live in the
  `PricingTool` schema. Migrations apply automatically on startup
  (or manually: `dotnet ef database update --project PricingTool.Data`).
- **`SourceReadOnly`** — the live platform **server** (hosting `GjirafaMall`, `GjirafaEcommerce`
  and the shared `GjirafaTranslations`). Use a SQL login with **read-only / execute-only** rights;
  add `ApplicationIntent=ReadOnly` where applicable. The operational database is selected per layer
  by substituting the catalog name into the query, so the connection's `InitialCatalog` is
  irrelevant as long as the login can read all three databases.

No secrets in code — use environment variables or a secret store for real credentials. (For local
dev, `EnvFileLoader` reads a gitignored `.env` with `SOURCE_DB_*` values and flips demo mode off.)

### 2. Deploy the dataset stored procedure (optional)

Run [`scripts/usp_GetDailyPricingDataset.sql`](scripts/usp_GetDailyPricingDataset.sql) on the
source server and grant `EXECUTE` to the read-only login. It takes the per-layer parameters
(`@StoreId`, `@TranslationCountryId`, `@WarehouseStoreId`, `@FilterVendors`, `@ExcludeUnpublished`).

**Important:** the compiled procedure reads the `GjirafaMall` operational database by name and
**cannot switch to `GjirafaEcommerce`**, so **Gjirafa50 layers require inline-query mode**. Set
`SourceDataset:Mode = "InlineQuery"` and the tool runs the same query verbatim, substituting the
operational database per layer (`{opDb}` token). Inline mode is the source of truth.

### 3. Turn off demo mode

```json
"PricingEngine": { "UseDemoData": false }
```

### 4. Run

- `PricingTool.Web` — admin UI/dashboard (also serves on-demand runs).
- `PricingTool.Engine` — host as a Windows service / container for the scheduled daily runs
  (iterates active layers).

## Configuration reference (`PricingEngine` section)

| Key | Default | Meaning |
|---|---|---|
| `VatRatePct` | `18.0` | Default/fallback VAT. The **effective rate is per layer** (`Layer.VatRatePct`: 18 for KS/MK, 20 for AL). Prices **and** PPTCV (the all-in landed cost: purchase + transport + customs + VAT) are VAT-**inclusive**, so margin = (price − PPTCV) / price with no VAT conversion. VAT is used only to gross up VAT-exclusive sales revenue (e.g. the average-selling-price column). |
| `StockoutRiskDays` | `14` | Algorithm 4 horizon: projected sellout within N days + healthy margin → vote discount off |
| `NewProductProtectionDays` | `90` | Unused — new-product protection now uses the platform `MarkAsNew` window, not this config. |
| `ChangeConfirmationThresholdPct` | `20.0` | Proposals beyond ±this % require explicit confirmation at approval (enforced server-side) |
| `UseDemoData` | `true` | Replace the SQL source reader with the demo generator |
| `SourceDataset:Mode` | `StoredProcedure` | `InlineQuery` runs the query verbatim — **required for Gjirafa50 layers** |
| `DefaultRunTimeUtc` / `DefaultCadenceHours` | `03:00` / `24` | Seed the schedule on first boot; afterwards each layer's schedule is edited in the UI (stored **per layer** on the `Layers` row) |
| `PushExportDirectory` | `exports` | Where the v1 CSV push integration writes approved prices |

Per-band knobs (margin floor, optional blended-margin **target**, rounding convention + toggle,
per-algorithm enable/weight 0–100) are **data**, edited per layer on the Price Bands page. Per-SKU
rounding opt-outs live in `SkuOverrides` (scoped per layer).

## How a pricing run works

1. Pull the daily dataset for the layer over the read-only connection (one row per in-scope SKU),
   scoped by the layer's store/country IDs, vendor filter, and the outlet / unpublished exclusions.
   Prices come back in the layer's **native currency** (EUR/MKD/ALL) — no FX conversion.
2. Snapshot the full pull into `DailySnapshots` (history for the dashboard + aging signals;
   a same-day re-pull replaces that layer's snapshot for the day, and proposals keep their own copy
   of inputs).
3. Per SKU: skip & flag if cost is NULL (**never treated as zero**), price missing, or no band
   matches (**bands key off PPTCV/cost**, not the selling price); otherwise run every
   band-enabled algorithm → weighted average of votes (band weight × vote confidence) →
   **guardrail clamp** (margin floor on the all-in cost = PPTCV/(1−floor%) + a ceiling at the higher of the
   anchor/FinalPrice and the current price — a price already above the anchor is held, not forced down —
   falling back to the shelf price + a flag when FinalPrice is missing; no discount ceiling; locally-held dead stock may pierce
   the floor down to an age-tiered cost floor (100% of cost when freshly dead, deepening to 40% of cost for the oldest stock), and a below-floor price that starts selling is held there) →
   **psychological rounding** that never violates the guardrails.
   When **no algorithm votes** (the common case on a layer running a narrow roster), the floor is still
   *audited* rather than skipped: the CURRENT price is run through the same clamp, so one already below the
   floor is proposed at the floor (`CURRENT_BELOW_FLOOR_LIFTED`) instead of passing through unpriced —
   catching campaign discounts cutting past cost, cost rises and stale anchors. Deliberate below-floor
   prices (dead-stock tunnel, tunnel-held, clearance-locked) are left exactly where they are — but they now
   RECORD the guardrail that is holding them (`CLEARANCE_LOCK_HELD`, `DEAD_STOCK_TUNNEL_HELD`,
   `DEAD_STOCK_BELOW_STAGE_HELD`, `DEAD_STOCK_FLOOR_RELAXED`), so a price deliberately pinned below cost is
   filterable and legible instead of being written as a blank row indistinguishable from an untouched SKU.
4. Write `ProposedPrices` + every `AlgorithmVotes` row, wrapped in a `PricingRuns` record
   (status, SKU/error counts) — failures and partial runs stay visible.
5. Humans take it from there: review → approve → push (CSV export via `IPricePushService`).

### Psychological rounding

Selected per band (per layer), and always clamped inside the band guardrails:

| Convention | Use |
|---|---|
| `.99` / `.95` endings, whole number, 995-style (€5 steps) | EUR layers |
| **…99 whole-currency** (e.g. 6149 → 6199, 9990 → 9999) | MKD / ALL layers — `.99`/`.95` are meaningless for currencies with no minor unit |

Under €5 the `.99` grid tightens to a 10-cent `.x9` grid (…0.99, 1.09, 1.19) so cheap items don't
swing between 0.99 and 1.99 and distort margin (threshold: `PricingEngine:LowPriceRoundingThreshold`).

### The 4 algorithms

`SELL_THROUGH`, `DEAD_STOCK`, `ELASTICITY`, `CROSS_DOCK` — each an `IPricingAlgorithm` in
`PricingTool.Core/Algorithms`, individually toggleable and weighted per band. Algorithms return
`null` when they have no opinion; if nothing votes, the price stays unchanged. (Consolidated from
the original 10: `SELL_THROUGH` merges velocity-forecast + stockout-risk + momentum; `STOCK_AGING`,
`SUPPLIER_LOCAL`, `DISCOUNT_EFFECTIVENESS` and `MARGIN_TIER` were retired — margin is a guardrail
constraint, not a reason to move price; `NEW_PRODUCT` is no longer an algorithm — new-product
protection is now a hard engine rule from the platform MarkAsNew window. `CROSS_DOCK` was later
added for supplier-fulfilled SKUs, replacing the old supplier-only-no-markdown guardrail.)

Aging ("consecutive no-sale days of no movement") is derived from the tool's own snapshot history and
measured in **calendar days**: each run advances the counter by the days elapsed since the last
snapshot (a no-sale gap extends it; a sale within the gap-covering window resets it). Because it is in
real days, it means the same thing at any run cadence — `DEAD_STOCK`'s 5pp-per-2-week progression and
the age stages hold whether the engine runs every 24h or every 15 days. The engine runs **daily / 24h**
by default (see `PricingEngine:DefaultCadenceHours`); the current cadence for existing layers lives on
`Layers.CadenceHours`.

**Slow lanes (15-day rhythm).** Sell-through, the guardrails, the margin-target and rounding react on
every daily run, but the two "slow" signals — the fitted price-elasticity vote and the `DEAD_STOCK`
progressive markdown — only form a fresh opinion every `PricingEngine:SlowLaneCadenceDays` (default 15).
On the daily runs in between they are **frozen (freeze-and-reuse, not abstain)**: the dead-stock pacer
re-emits its already-applied target and does **not** step (`SkuContext.SlowLanesFrozen`, stamped off
`Layers.LastSlowLaneRunUtc`), so those prices stay put — abstaining would drop the markdown out of the
weighted blend and let the price bounce back up (a sawtooth). Elasticity is frozen independently by its
own refit cadence, `PricingEngine:ElasticityFitCadenceDays` (default 15) — the fitted coefficient is the
lane's opinion, so holding it between refits holds the vote.

**`yz` SKUs are priced.** SKUs whose code ends in `yz` are local-supplier products sourced through
the `YZATT` vendor. They used to be priced by hand, and were skipped twice over: the daily query
dropped any `Sku LIKE '%yz'`, and the GjirafaMall vendor allowlist left `YZATT` out. Both are gone
-- the SKU filter and the vendor omission -- so they now price like any other SKU on all six
layers. `YZATT`'s whole catalogue is `yz`-suffixed, so admitting the vendor adds nothing else.
Every one of them is listed on **both** brands, so on GjirafaMall they are priced through
`SYNCED_TO_GJ50` -- Gjirafa50 decides the price and GjirafaMall adopts it verbatim.

`ELASTICITY` acts only on **confidently-elastic** SKUs: the weekly fit stores the slope **and its
standard error**, and the profit-max price `cost·E/(E+1)` is voted only when `E + 1.645·SE ≤ −1` (the
whole one-sided 95% interval is below −1). This silences noisy near-unit fits whose markup would
explode (e.g. −1.18 ± 0.6 → ×6.6); the vote is additionally **capped at the anchor**. It abstains on
locally-held dead stock: the 365-day fit window can straddle sales that predate the 90-day dead gate,
but zero movement for 90 days refutes that demand curve — the paced dead-stock markdown owns the lane
alone (a co-vote would dilute its no-jump pacing).

`DEAD_STOCK` is the only algorithm allowed below the margin floor: locally-held stock with no sales
in 90 days whose oldest on-hand unit is ≥ 90 days old gets a markdown **paced from the live
discount** (`DeadStockMarkdownPacer`): it opens one step below the current discount (or at the band's
minimum start discount if deeper) and deepens one step per period **counted from when the shop price
actually reflects the previous drop** — never a catch-up jump off the raw streak, and unapplied
proposals just stay re-proposed, they don't deepen. The pacing state (target + period clock) lives on
`SkuDeadStockStates` and resets when the SKU sells or stops qualifying as dead. The below-floor
**cost floor** deepens with the stock's **observed dead-age tier** — `DeadObservedDays` (consecutive
no-sale snapshot days counted forward, not backfilled from pre-tracking history — though a SKU first seen
already stale, with no 90-day sales and genuinely old WMS stock, seeds from that stock age so it isn't
reset to Stage 1), **not** WMS warehouse age, which only gates freshness. Four per-band stages, all editable (step and period are per-stage too, though equal by
default): **3–6 months** (below 180 observed days, floor 100% of cost), **6–12 months** (from 180d,
80%), **12–36 months** (from 360d, 60%), **36 months+** (from 1080d, 40%), each with its own step
(default 5pp) and period (default 14 zero-sale days). Enforced in `GuardrailService` (flag
`DEAD_STOCK_FLOOR_RELAXED`),
so it's gated on the dead-stock context, not on which algorithm voted. Once such a below-floor price
starts selling again it's frozen at that level (`DEAD_STOCK_TUNNEL_HELD`) — never raised back. That freeze
requires *provenance* (`SkuDeadStockState.BelowFloorTunnelPrice`: the lane really did take this SKU below
the floor), so a price that merely fell under a floor that **moved** — a cost rise, a stale anchor — is
repaired by the margin floor instead of being mistaken for a deliberate clearance markdown.

`CROSS_DOCK` (weight 40) is the lane for **supplier-fulfilled** SKUs — `KsStock == 0 && SupplierStock > 0`,
the ones the other advisors skip (sell-through/dead-stock need local stock; elasticity often can't fit). A
sell-through/dead-stock hybrid: it **holds** the working price when the SKU is selling (`Qty90 > 0`) and
runs a **soft progressive markdown toward the band margin floor** when it isn't (`Qty90 == 0`, opening at
5% and deepening 5pp every ~3 weeks). Unlike dead-stock it's bounded by the **normal** margin floor, never
the below-floor tunnel — nothing is sunk to recover. It's monotonic (never raises, except lifting an
underwater price up to the floor) and low-weighted so it defers to `ELASTICITY` when that fires. Adding it
**removed** the old supplier-only-no-markdown freeze: supplier stock is now priced, not frozen. See
[`docs/algorithms/cross-dock.md`](docs/algorithms/cross-dock.md).

**Margin-target guardrail.** A band can carry an optional blended-margin **goal** (`TargetMarginPct`).
When set, a post-pass (`MarginTargetService`) nudges *only* the SKUs no algorithm moved (the "free"
ones) — trimming from the FinalPrice anchor toward the goal — so the band's overall blended margin
approaches its target without overriding any algorithm's explicit vote. It stays inside the same
floor/anchor bounds as everything else and is treated as "on goal" within `MarginTargetTolerancePct`
(default 1pp); flags `MARGIN_TARGET_ADJUSTED` / `_TRIM` / `_LIFT` / `_UNREACHABLE`. Off when the
target is null.

**Reporting.** Movers shows each SKU's average selling price per 7d/30d/90d window (gross, VAT incl.).
Both Movers and Proposals show profit &amp; margin KPI cards — now → proposed over 7d/30d/90d, using a
naive same-quantity baseline (assume the trailing window's units sell again at the current vs proposed
price; VAT-net, cost-known SKUs only). Proposals' cards track the active filters; Movers' summarise the
listed movers. The math lives in `KpiMath.FromSums` (shared by the in-memory Movers path and the
single-round-trip Proposals SQL aggregate). The Proposals list — and its filter-aware CSV export —
also carry each SKU's supplier, current local-warehouse stock, and trailing 7d/30d unit sales from
the run's snapshot day.

## Full-catalog scale

A real run prices the **entire** catalog (~660k in-scope SKUs per GjirafaMall layer after the
outlet filter), so the write/read path is built for volume:

- Snapshots, proposals and votes are written via **`SqlBulkCopy`** (`BulkWriteService`), not EF
  row-by-row. A full live run completes in a few minutes.
- The same-date snapshot replace **batches** its delete (50k/iteration) and all run-path SQL runs
  on a 600s command timeout (the run's DbContext is scoped, so the web UI keeps fast-fail timeouts).
- The Proposals listing sorts by change magnitude via a DB-computed `AbsChangePct` column + index,
  so "top 500 by magnitude" is an index range scan, not a half-million-row live sort.
- Outcome evaluation batch-loads each pushed SKU's snapshot history once (no per-proposal N+1).

- Dead-stock aging no longer scans 180 days of history: `SaveSnapshotAsync` maintains a running
  per-(layer, SKU) `SkuDeadStockStates` counter (zero-Qty7 day extends it, a sale resets it;
  idempotent on a same-date re-pull), and `GetZeroSaleStreaksAsync` just reads it.

> Known follow-up: full-catalog daily runs still grow `PricingToolDb` by ~1.4M+ rows/run — snapshot
> retention/TTL + a `DailyLayerKpi` rollup are the next steps of the storage-growth plan (the
> dead-stock counter above is step 1, which removed the 180-day-history requirement).

## Authentication

The app uses Gjirafa's **Porta** SSO (OpenID Connect, authorization-code flow + PKCE) on a cookie
session — the same pattern as the sibling Finance app. Porta only *authenticates* the person; their
**role** comes from the local **Users allowlist** in the tool's database. A Porta account that isn't
on the list — or is inactive — is denied. Wiring lives in
[`PortaAuthExtensions`](PricingTool.Web/Services/PortaAuthExtensions.cs).

**Local development:** leave `Porta:Authority`/`Porta:ClientId` blank (the default in
[`appsettings.json`](PricingTool.Web/appsettings.json)) and the app falls back to the
[`DevAuthHandler`](PricingTool.Web/Services/DevAuthHandler.cs) no-auth shim — every request is signed
in as a `demo` user holding both roles, so the app opens with no login. The navbar shows a `no-auth`
badge in this mode.

**Production:** the `Porta__*` settings are set inline on [web-deployment.yaml](k8s/web-deployment.yaml)
(Authority, ClientId, ClientSecret, CallbackPath, InitialAdminEmail), mirroring the Finance app. It
reuses Finance's Porta client, so that client must have this app's redirect URIs registered in Porta:
`https://dynamic-pricing.gjirafa.dev/signin-oidc` and `…/signout-callback-oidc`. On first startup the
initial Manager (`Porta__InitialAdminEmail`; empty by default in the hackathon repo) is seeded so the app is usable
immediately; Managers add everyone else via **Users** in the navbar.

### Roles

- **Analyst** — view everything, trigger on-demand runs (simulations; runs only produce proposals).
- **Manager** — everything + edit bands/schedule, approve/reject, push, and **manage users**.

## Tests

```bash
dotnet test
```

## Open decisions (v1 defaults implemented)

1. **Objective function nuance** — strict total-profit max vs margin tie-break: deferred to
   configuration via per-band algorithm weights.
2. **Product launch/creation date source** — dataset has none; the snapshot column is nullable
   and Algorithm 2 stays silent until it's populated.
3. **Price band boundaries** — seeded €0–10 … 1,000+ are **placeholders** (editable per layer);
   bands for MKD/ALL layers are seeded with the same numeric structure and should be **retuned in
   each currency** before go-live.
4. **Live-source verification for non-KS layers** — the MK/AL/Gjirafa50 store/country IDs are as
   provided; confirm them (and that `Product.IsOutlet` / `UnpublishedStoreids` exist in both
   operational DBs) with a real run per layer before relying on them.
5. **Competitor pricing / seasonality** — phase 2 (no competitor data; seasonality needs >90d of
   accumulated snapshots, which this tool is now collecting).
6. **Push mechanism into NopCommerce** — `IPricePushService` is the integration point; v1 ships
   `CsvPricePushService` (file export). The platform team replaces the DI registration in
   `PricingTool.Data/DependencyInjection.cs` when the real write-back mechanism is decided.

## Safety rules recap (non-negotiable, implemented)

1. Engine writes proposals only; the push step is explicit, human, Manager-gated.
2. Source data via a dedicated read-only connection; tool tables in their own DB/schema.
3. Every pull snapshotted (`DailySnapshots`), scoped per layer.
4. Every config change and every push audited (who/what/when/old/new, with layer) — Audit Log page.
5. Every run wrapped in a `PricingRuns` record with status and error counts.
