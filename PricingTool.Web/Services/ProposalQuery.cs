using System.Linq.Expressions;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;

namespace PricingTool.Web.Services;

/// <summary>
/// Pure, HttpContext-free composition of the Proposals listing query — the row-level column filters
/// (the "Excel" per-column filtering) and the column sort map. Kept out of the controller so the
/// predicates are unit-testable against an in-memory DbSet with no auth/session scaffolding, and so the
/// EXACT same filtering flows through the on-screen listing, the CSV exports, and "Approve all (filtered)"
/// (all of which run <see cref="Apply"/>). Snapshot-backed columns (7d/30d sales, local stock) live in
/// DailySnapshots, not on the proposal row, so their FILTERS ride a semi-join in <see cref="ApplySnapshotFilters"/>
/// and their SORT rides a join handled by the caller (see <see cref="IsSnapshotSort"/>); both take the run's
/// snapshot day from the caller so the pure query here never has to reach into another table.
/// </summary>
public static class ProposalQuery
{
    /// <summary>Applies every row-level filter in <paramref name="filter"/> to the run's proposals.</summary>
    public static IQueryable<ProposedPrice> Apply(IQueryable<ProposedPrice> query, ProposalsFilter filter)
    {
        query = query.Where(p => p.PricingRunId == filter.RunId);

        if (!string.IsNullOrWhiteSpace(filter.Sku))
        {
            var term = filter.Sku.Trim();
            query = query.Where(p => p.Sku.Contains(term));
        }

        if (Enum.TryParse<ProposalStatus>(filter.Status, out var status))
            query = query.Where(p => p.Status == status);

        if (filter.BandId.HasValue)
            query = query.Where(p => p.PriceBandId == filter.BandId);

        if (filter.SupplierId.HasValue)
            query = query.Where(p => p.SupplierId == filter.SupplierId);

        if (!string.IsNullOrWhiteSpace(filter.Brand))
        {
            var brand = filter.Brand.Trim();
            query = query.Where(p => p.Brand != null && p.Brand.Contains(brand));
        }

        if (!string.IsNullOrWhiteSpace(filter.ProductName))
        {
            var name = filter.ProductName.Trim();
            query = query.Where(p => p.ProductName != null && p.ProductName.Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(filter.Guardrails))
        {
            var g = filter.Guardrails.Trim();
            query = query.Where(p => p.GuardrailFlags.Contains(g));
        }

        // Dead-stock stage: −1 = any stage (1–4), 0 = not dead, 1–4 = that exact stage.
        if (filter.DeadStockStage is int stage)
            query = stage == -1
                ? query.Where(p => p.DeadStockStage != DeadStockStage.None)
                : query.Where(p => p.DeadStockStage == (DeadStockStage)stage);

        if (!string.IsNullOrEmpty(filter.Algorithm))
            query = query.Where(p => p.ReasonCodes.Contains(filter.Algorithm) ||
                                     p.Votes.Any(v => v.AlgorithmCode == filter.Algorithm));

        // Signed Δ% range (the on-screen Δ% column filter) — ACTUAL change, not magnitude. Negative = a
        // price drop, positive = a rise: Max = −20 keeps only drops of 20%+, Min = 0 keeps only rises.
        if (filter.MinChangePct is decimal minChg)
            query = query.Where(p => p.ChangePct >= minChg);
        if (filter.MaxChangePct is decimal maxChg)
            query = query.Where(p => p.ChangePct <= maxChg);

        // Upper bound on |Δ%| (the "normal changes only" export's magnitude cap): drop the large swings.
        if (filter.MaxAbsChangePct.HasValue)
            query = query.Where(p => p.AbsChangePct <= filter.MaxAbsChangePct.Value);

        // ---- Per-column numeric ranges ------------------------------------------------------------
        if (filter.CostMin is decimal costMin) query = query.Where(p => p.Pptcv != null && p.Pptcv >= costMin);
        if (filter.CostMax is decimal costMax) query = query.Where(p => p.Pptcv != null && p.Pptcv <= costMax);
        if (filter.OldMin is decimal oldMin) query = query.Where(p => p.OldPrice >= oldMin);
        if (filter.OldMax is decimal oldMax) query = query.Where(p => p.OldPrice <= oldMax);
        if (filter.AnchorMin is decimal anchorMin) query = query.Where(p => p.AnchorPrice >= anchorMin);
        if (filter.AnchorMax is decimal anchorMax) query = query.Where(p => p.AnchorPrice <= anchorMax);
        if (filter.CurrentMin is decimal curMin) query = query.Where(p => p.CurrentPrice >= curMin);
        if (filter.CurrentMax is decimal curMax) query = query.Where(p => p.CurrentPrice <= curMax);
        if (filter.ProposedMin is decimal propMin) query = query.Where(p => p.ProposedPriceValue >= propMin);
        if (filter.ProposedMax is decimal propMax) query = query.Where(p => p.ProposedPriceValue <= propMax);

        // ---- Margin ranges (percent). Cross-multiplied against the guarded positive price to dodge
        //      division / divide-by-zero and stay index-friendly. Only cost-known rows can match — so
        //      "Prev margin ≤ 0" is exactly the below-/at-cost set (Cost ≥ Current). -----------------
        if (filter.PrevMarginMin is decimal pmMin)
            query = query.Where(p => p.Pptcv != null && p.CurrentPrice > 0 &&
                                     (p.CurrentPrice - p.Pptcv.Value) * 100m >= pmMin * p.CurrentPrice);
        if (filter.PrevMarginMax is decimal pmMax)
            query = query.Where(p => p.Pptcv != null && p.CurrentPrice > 0 &&
                                     (p.CurrentPrice - p.Pptcv.Value) * 100m <= pmMax * p.CurrentPrice);
        if (filter.NewMarginMin is decimal nmMin)
            query = query.Where(p => p.Pptcv != null && p.ProposedPriceValue > 0 &&
                                     (p.ProposedPriceValue - p.Pptcv.Value) * 100m >= nmMin * p.ProposedPriceValue);
        if (filter.NewMarginMax is decimal nmMax)
            query = query.Where(p => p.Pptcv != null && p.ProposedPriceValue > 0 &&
                                     (p.ProposedPriceValue - p.Pptcv.Value) * 100m <= nmMax * p.ProposedPriceValue);

        // Active-discount-id presence. Explicit null/"" checks (not string.IsNullOrEmpty) so both the
        // SQL Server and the in-memory providers translate it.
        if (filter.DiscountIds == "blank")
            query = query.Where(p => p.ActiveDiscountIds == null || p.ActiveDiscountIds == "");
        else if (filter.DiscountIds == "nonblank")
            query = query.Where(p => p.ActiveDiscountIds != null && p.ActiveDiscountIds != "");

        // Gjirafa50-sync: on a GjirafaMall run the SKUs shared with the same-country Gjirafa50 layer adopt
        // Gj50's price verbatim (SYNCED_TO_GJ50 is stamped FIRST in ReasonCodes, see PricingRunOrchestrator).
        // Mall never prices those itself, so "exclude" leaves only the rows this layer actually decided.
        // A substring test is safe — no other reason/guardrail code contains this token.
        if (filter.Gj50Sync == "exclude")
            query = query.Where(p => !p.ReasonCodes.Contains(GuardrailFlags.SyncedToGj50));
        else if (filter.Gj50Sync == "only")
            query = query.Where(p => p.ReasonCodes.Contains(GuardrailFlags.SyncedToGj50));

        // Platform-new products (MarkAsNew window). Uses the true IsNewProduct signal stamped on the row,
        // so it catches new products whether the layer held them (NEW_PRODUCT_PROTECTED) or priced them.
        if (filter.NewProduct == "only")
            query = query.Where(p => p.IsNewProduct);
        else if (filter.NewProduct == "exclude")
            query = query.Where(p => !p.IsNewProduct);

        if (filter.ChangedOnly && filter.Status == "Pending")
            query = query.Where(p => p.HasChange);

        return query;
    }

    /// <summary>
    /// Applies the snapshot-backed column ranges — local stock and 7d/30d unit sales — which can't live in
    /// <see cref="Apply"/> because those values are in <see cref="DailySnapshot"/>, not on the proposal row.
    /// Each bound is an EXISTS semi-join against <paramref name="snapshot"/> (the run's snapshot day, scoped
    /// by the caller), so the result stays an <see cref="IQueryable{T}"/> of <see cref="ProposedPrice"/> and
    /// composes on the SAME shared path as the listing, the CSV exports and "Approve all (filtered)". A SKU
    /// with no snapshot row can't satisfy a bound — value unknown — mirroring how the cost range in
    /// <see cref="Apply"/> excludes cost-unknown rows. A no-op (returns the query unchanged) when no
    /// snapshot bound is set, so the common case adds no EXISTS to the plan.
    /// </summary>
    public static IQueryable<ProposedPrice> ApplySnapshotFilters(
        IQueryable<ProposedPrice> query, IQueryable<DailySnapshot> snapshot, ProposalsFilter filter)
    {
        if (filter.LocalStockMin is int lsMin)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.LocalWarehouseStock >= lsMin));
        if (filter.LocalStockMax is int lsMax)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.LocalWarehouseStock <= lsMax));
        if (filter.Qty7Min is int q7Min)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.Qty7 >= q7Min));
        if (filter.Qty7Max is int q7Max)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.Qty7 <= q7Max));
        if (filter.Qty30Min is int q30Min)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.Qty30 >= q30Min));
        if (filter.Qty30Max is int q30Max)
            query = query.Where(p => snapshot.Any(s => s.Sku == p.Sku && s.Qty30 <= q30Max));
        return query;
    }

    // ---- Sorting --------------------------------------------------------------------------------

    private static readonly HashSet<string> SnapshotColumns =
        new(StringComparer.OrdinalIgnoreCase) { "qty7", "qty30", "localstock" };

    /// <summary>True when the sort targets a DailySnapshots-backed column (caller must join + sort).</summary>
    public static bool IsSnapshotSort(string? sort) => SnapshotColumns.Contains(ParseSort(sort).Column);

    /// <summary>Normalises a sort token to <c>(column, descending)</c>, mapping the four legacy values.</summary>
    public static (string Column, bool Desc) ParseSort(string? sort)
    {
        switch ((sort ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "change_desc": return ("abschange", true);
            case "change_asc": return ("change", false);
            case "sku": return ("sku", false);
            case "price_desc": return ("proposed", true);
        }

        var s = sort!.Trim();
        var i = s.LastIndexOf('_');
        return i > 0
            ? (s[..i].ToLowerInvariant(), s[(i + 1)..].Equals("desc", StringComparison.OrdinalIgnoreCase))
            : (s.ToLowerInvariant(), false);
    }

    /// <summary>Applies a non-snapshot column sort. Snapshot columns fall through to the default
    /// (index-backed |Δ%| desc) — the caller sorts those with the snapshot join instead.</summary>
    public static IQueryable<ProposedPrice> ApplySort(IQueryable<ProposedPrice> query, string? sort)
    {
        var (col, desc) = ParseSort(sort);
        return col switch
        {
            "productid" => Order(query, p => p.ProductId, desc),
            "sku" => Order(query, p => p.Sku, desc),
            "name" => Order(query, p => p.ProductName, desc),
            "brand" => Order(query, p => p.Brand, desc),
            "supplier" => Order(query, p => p.SupplierName, desc),
            "band" => Order(query, p => p.PriceBandId, desc),
            "cost" => Order(query, p => p.Pptcv, desc),
            "old" => Order(query, p => p.OldPrice, desc),
            "anchor" => Order(query, p => p.AnchorPrice, desc),
            "current" => Order(query, p => p.CurrentPrice, desc),
            "proposed" => Order(query, p => p.ProposedPriceValue, desc),
            "change" => Order(query, p => p.ChangePct, desc),
            // Margin fraction; cost-unknown / non-positive-price rows collapse to null (SQL sorts them
            // to the end on desc, the front on asc — acceptable for a display sort).
            "prevmargin" => Order(query, p => p.Pptcv != null && p.CurrentPrice > 0
                ? (decimal?)((p.CurrentPrice - p.Pptcv.Value) / p.CurrentPrice) : null, desc),
            "newmargin" => Order(query, p => p.Pptcv != null && p.ProposedPriceValue > 0
                ? (decimal?)((p.ProposedPriceValue - p.Pptcv.Value) / p.ProposedPriceValue) : null, desc),
            "discountids" => Order(query, p => p.ActiveDiscountIds, desc),
            "status" => Order(query, p => p.Status, desc),
            "abschange" => Order(query, p => p.AbsChangePct, desc),
            _ => query.OrderByDescending(p => p.AbsChangePct),
        };
    }

    private static IQueryable<ProposedPrice> Order<TKey>(
        IQueryable<ProposedPrice> query, Expression<Func<ProposedPrice, TKey>> key, bool desc)
        => desc ? query.OrderByDescending(key) : query.OrderBy(key);
}
