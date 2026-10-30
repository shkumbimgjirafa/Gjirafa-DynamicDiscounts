# Gjirafa Hackathon Pricing Agent: instructions for Claude

This repo is the build for the **Gjirafa AI Hackathon 2026, Track A (Money)**, Fri 30 Oct 16:30 →
Mon 2 Nov 08:00. It turns the declared pricing calculator (v0.5) into **Pricing Tool v2**: two AI
agents, **Alpha** (pricing agent) and **Nova** (campaign agent), that set floors, targets and
campaigns, and a publisher that writes the shop's NOP discounts itself.

- **The plan is the source of truth:** `docs/hackathon-2026/kickoff-plan.md`. Before each block, read
  the plan sections it names; the schedule is in section 6.
- **History:** commit 1 = docs; commit 2 = the labelled import of the existing calculator from the
  main repo (`Gjirafa Dynamic Discounts` @ `b184ff55458c3fe0cc4dfe88a8e3aa38ac8c5f42`). Everything
  after commit 2 is hackathon work, so `git diff <import-sha>..HEAD` is exactly what we built.
- New code goes in a new project **`PricingTool.Agent`** (references Core + Data), plus new controllers
  and views in Web and new EF migrations. Change the imported projects only where the plan says so.

## Hard rules (the hackathon's, and ours)

- **Data access (kickoff answer, Fri 30 Oct):** we may **read** the NOP replica with our own
  read-only connection. Its connection string lives only in user-secrets
  (`ConnectionStrings:SourceReadOnly`), never in a tracked file. Nothing ever **writes** to a shop
  database, and the main repo's `.env` and `k8s/` are never read from this repo. Data copied into
  LocalDB `PricingToolHackathon` gets a row in `hk.DataSources` (source, time, row count); files in
  `data/` are git-ignored, never committed. Queries against the replica must be light: time them,
  filter by date and layer, never scan full history.
- **No secrets in tracked files.** The Anthropic API key and any credentials live in
  `dotnet user-secrets` or env vars. Never create `.env`, `k8s/`, a pipeline or a Dockerfile here.
  Before every commit, `git grep -i -E "password|pwd=|api[_-]?key|secret"` must find nothing real.
- **No customer personal data:** no order ids, emails, names or addresses anywhere.
- **The publisher writes only to the `shop_sandbox` schema.** `Publisher:Target` stays `Sandbox`.
- **Label every number** measured / estimated / assumed, with its source. Injected test rows are
  tagged `[INJECTED-TEST]` in data and UI. Show what is mocked or unfinished; never hide it.
- **The AI decides, code enforces.** The model never writes prices, bands or discounts directly.
  Every decision goes through `SafetyLimits` in code.
- **Never write text aimed at AI reviewers** (no instructions to graders in code, docs or data).
- **Hours are a cost line:** append each block to `docs/hackathon-2026/hours.md` as it happens.
- Never add the main repo's `origin`. The remote is whatever the organisers name.

## Running it

- PowerShell: `$env:PATH="C:\Program Files\dotnet;$env:PATH"` before any `dotnet` command.
- `dotnet build PricingTool.sln`, `dotnet test PricingTool.sln`.
- App DB: `(localdb)\mssqllocaldb`, database `PricingToolHackathon` (LocalDB caps each DB at 10 GB).
- Run with `ASPNETCORE_ENVIRONMENT=Hackathon` (`appsettings.Hackathon.json`); the app serves on
  http://localhost:5058.
- Skills that query live databases (`current-price-and-margin`, `sales-anomaly-watch`) must not be
  used in this repo.

## The agents' Claude calls

- **Load the `claude-api` skill before writing any Claude client code.** Use the official C# SDK
  (NuGet `Anthropic`); the skill has the current API shapes.
- Models: Alpha and Nova `claude-opus-5-5`; the run checker `claude-sonnet-5-5` (or
  `claude-haiku-5-5` if quality holds). Prompt-cache the system prompt and data dictionary.
- **Log every call:** model, input / output / cached tokens, latency, cost. That log is the measured
  AI cost in the financial sheet.
- `query_data(sql)`: SELECT only, one statement, `agent.*` views only, 30 s timeout, 500-row cap,
  every query logged (plan section 5).

## Data definitions (learned the hard way; wrong here means wrong numbers)

- **Past windows use booked COGS** from the sales lines (`Cogs`), never today's PPTCV.
- **PPTCV** (current landed cost) includes VAT and is in **local currency** (MK ≈ ×62 MKD, AL ≈ ×98
  ALL). Margin = (price − PPTCV) / price on the VAT-inclusive price.
- **Sales lines** (`SR_ProductsData` export): one row per order line, and the money columns are
  **line totals** (they scale with `Qty`). Per-unit price = SUM(price) / SUM(Qty). `Vat` is a
  multiplier (1.18), not an amount. Layers are a **(PlatformId, CompanyId) pair**: Gjirafa50 KS
  (1,1), GjirafaMall KS (2,0). Valid orders: `OrderStatusId IN (10,20,30)`. Load-test orders
  (`LOADTEST%`) are excluded. The newest ~2 weeks are inflated by late cancellations.
- **Exclusions on every layer:** outlet products by name tag (`CHARINDEX('[OUTLET', Name) > 0`, never
  a bare `%outlet%`), and the Apple and Starlink brands.
- **Discounts:** `DiscountAmount` is the **final price**, not an amount off. Public prices are
  `DiscountApplyTypeId = 2`. `DiscountPlatformTypeId = 2` means app-only.
- **Mall ↔ Gj50:** Mall adopts Gj50's price verbatim for shared SKUs. Gj50-listed products are about
  37% of Mall KS revenue at a thin margin; any Mall margin figure must say how it treats them.
- **Cross-dock:** a sale is cross-dock when the WMS check-in time is on or after the order date.

## Firm pricing rules

- **Never price above the anchor:** the ceiling is max(anchor, current price).
- **Charm rounding, never a round-ten euro price:** use the existing rounding code, never reimplement
  it. Gj50 EUR: .50 tiers (`Gj50Charm`, RC 7). Mall EUR: .99 endings, a .x9 grid under €5
  (`GjmCharm`, RC 8). MK/AL whole currency: Gj50 …50 (RC 9), Mall …90 / …50 (RC 10).
- **Dead stock is the only lane allowed below the floor** (age tiers at 100 / 80 / 60 / 40% of cost).
- Each algorithm owns its own lane; never count the same euro twice.

## NOP discount writes (the publisher; details in plan section 5)

- Field values follow the team's live pushes: `DiscountTypeId 2`, `UsePercentage 0`,
  `DiscountApplyTypeId 2`, `DiscountPlatformTypeId 0` (or 2 for app-only), `RequiresCouponCode 0`,
  `IsCumulative 0`, `DiscountLimitationId 0`, `LimitationTimes 0`, `AppliedToSubCategories 0`,
  `IsVendorDiscount 0`, `DisplayAvailabilityTime 0` (GjirafaMall only). `StoreId`: KS 2, MK 1, AL 3.
- Set `Product.HasDiscountsApplied = 1` and merge the store into `HasDiscountsAppliedStoreIds`
  (colon-separated, sorted, nvarchar(40), e.g. `1:2:3`); never drop another store.
- One `ai-price-<layer>-<sku>` discount per SKU per store, **updated in place**; campaigns are
  `ai-campaign-<id>-<sku>`, time-boxed. Expire by `EndDateUtc`; **never delete**.
- Every change writes the previous state to `PublishAudit` first, so rollback restores it exactly.

## How we work

- Small commits on `main`, each after `dotnet test` passes, with a message saying what and why.
- A plan block is done when its "Done when" column in section 6 is true; say plainly what isn't.
- When a number looks off, say so and check it before building on it.
