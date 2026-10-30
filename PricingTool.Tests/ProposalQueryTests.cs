using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Web.Models;
using PricingTool.Web.Services;

namespace PricingTool.Tests;

/// <summary>
/// Covers the pure row-level column filters and the sort-token parsing that drive the Proposals grid
/// (and, because they share <see cref="ProposalQuery.Apply"/>, the CSV exports and "Approve all
/// (filtered)"). Uses EF InMemory; deliberately avoids anything that depends on the AbsChangePct
/// computed column (InMemory leaves it 0).
/// </summary>
public class ProposalQueryTests
{
    private static PricingToolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PricingToolDbContext Seeded()
    {
        var db = NewDb();
        db.ProposedPrices.AddRange(
            Row(1, "A1", "Canon camera", "Canon", cost: 8m, current: 10m, proposed: 9m, discounts: "201,362", flags: ""),
            Row(2, "B2", "Puzzle box", "Tactic", cost: 15m, current: 10m, proposed: 11m, discounts: "", flags: "MARGIN_FLOOR"),
            Row(3, "C3", "Widget", "Acme", cost: null, current: 20m, proposed: 22m, discounts: null, flags: ""),
            Row(4, "D4", "Gadget", "Acme", cost: 5m, current: 5m, proposed: 5m, discounts: "999", flags: ""),
            Row(5, "E5", "Gizmo", "Canon", cost: 50m, current: 100m, proposed: 90m, discounts: "12", flags: "ROUND"));
        db.SaveChanges();
        return db;
    }

    private static ProposedPrice Row(
        long id, string sku, string name, string brand, decimal? cost, decimal current, decimal proposed,
        string? discounts, string flags, bool isNew = false) => new()
        {
            Id = id, PricingRunId = 1, LayerId = 1, Sku = sku, ProductName = name, Brand = brand,
            Pptcv = cost, CurrentPrice = current, ProposedPriceValue = proposed, OldPrice = current + 2m,
            AnchorPrice = current + 2m, ActiveDiscountIds = discounts, GuardrailFlags = flags,
            ReasonCodes = "", Status = ProposalStatus.Pending, HasChange = proposed != current,
            IsNewProduct = isNew,
        };

    // RunId + no status/changed-only filtering, so each test isolates one column predicate.
    private static ProposalsFilter F(Action<ProposalsFilter> set)
    {
        var f = new ProposalsFilter { RunId = 1, Status = "All", ChangedOnly = false };
        set(f);
        return f;
    }

    private static List<string> Skus(PricingToolDbContext db, ProposalsFilter f) =>
        ProposalQuery.Apply(db.ProposedPrices, f).Select(p => p.Sku).OrderBy(x => x).ToList();

    // Adds one snapshot day for four of the five seeded SKUs; E5 deliberately gets NO row so the tests
    // can assert that a SKU with an unknown snapshot value can't satisfy a snapshot bound.
    private static void SeedSnapshots(PricingToolDbContext db)
    {
        var day = new DateTime(2026, 07, 21);
        db.DailySnapshots.AddRange(
            Snap("A1", day, stock: 0, qty7: 0, qty30: 2),
            Snap("B2", day, stock: 5, qty7: 3, qty30: 10),
            Snap("C3", day, stock: 50, qty7: 0, qty30: 0),
            Snap("D4", day, stock: 200, qty7: 20, qty30: 90));
        db.SaveChanges();
    }

    private static DailySnapshot Snap(string sku, DateTime day, int stock, int qty7, int qty30) => new()
    {
        LayerId = 1, SnapshotDate = day, Sku = sku,
        LocalWarehouseStock = stock, Qty7 = qty7, Qty30 = qty30,
    };

    // Runs the shared listing path — Apply THEN ApplySnapshotFilters — exactly as the controller composes it.
    private static List<string> SnapSkus(PricingToolDbContext db, ProposalsFilter f)
    {
        var snapshot = db.DailySnapshots.Where(s => s.LayerId == 1);
        return ProposalQuery.ApplySnapshotFilters(ProposalQuery.Apply(db.ProposedPrices, f), snapshot, f)
            .Select(p => p.Sku).OrderBy(x => x).ToList();
    }

    // ---------------------------------------------------------------- Filters

    [Fact]
    public void NewProduct_Only_KeepsNew_Exclude_HidesNew_Null_NoFilter()
    {
        using var db = NewDb();
        db.ProposedPrices.AddRange(
            Row(1, "NEW1", "New launch", "Acme", cost: 10m, current: 20m, proposed: 20m, discounts: null, flags: "", isNew: true),
            Row(2, "OLD1", "Old sku", "Acme", cost: 10m, current: 20m, proposed: 18m, discounts: null, flags: ""),
            Row(3, "NEW2", "New launch 2", "Acme", cost: 10m, current: 30m, proposed: 30m, discounts: null, flags: "", isNew: true));
        db.SaveChanges();

        Assert.Equal(new[] { "NEW1", "NEW2" }, Skus(db, F(f => f.NewProduct = "only")));
        Assert.Equal(new[] { "OLD1" }, Skus(db, F(f => f.NewProduct = "exclude")));
        Assert.Equal(new[] { "NEW1", "NEW2", "OLD1" }, Skus(db, F(f => f.NewProduct = null)));
    }

    [Fact]
    public void PrevMargin_AtOrBelowZero_ReturnsBelowCostAndBreakEven_ExcludesCostUnknown()
    {
        using var db = Seeded();
        // (Current − Cost)/Current ≤ 0  ⇔  Cost ≥ Current. B2 (15≥10) and D4 (5=5); C3 has no cost.
        Assert.Equal(new[] { "B2", "D4" }, Skus(db, F(f => f.PrevMarginMax = 0m)));
    }

    [Fact]
    public void PrevMargin_Min_KeepsHealthyMargins()
    {
        using var db = Seeded();
        // ≥ 30%: A1 = 20%, E5 = 50%. Only E5 qualifies.
        Assert.Equal(new[] { "E5" }, Skus(db, F(f => f.PrevMarginMin = 30m)));
    }

    [Fact]
    public void NewMargin_Min_UsesProposedPrice()
    {
        using var db = Seeded();
        // (Proposed − Cost)/Proposed ≥ 40%: only E5 = (90−50)/90 ≈ 44%.
        Assert.Equal(new[] { "E5" }, Skus(db, F(f => f.NewMarginMin = 40m)));
    }

    [Fact]
    public void DiscountIds_Blank_ReturnsNullAndEmpty()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "B2", "C3" }, Skus(db, F(f => f.DiscountIds = "blank")));
    }

    [Fact]
    public void DiscountIds_NonBlank_ReturnsThoseWithIds()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "A1", "D4", "E5" }, Skus(db, F(f => f.DiscountIds = "nonblank")));
    }

    [Fact]
    public void ProposedRange_IsInclusive()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "C3", "E5" }, Skus(db, F(f => { f.ProposedMin = 20m; f.ProposedMax = 100m; })));
    }

    [Fact]
    public void ChangePct_SignedRange_FiltersActualValue_NotMagnitude()
    {
        using var db = NewDb();
        // ChangePct is a real (settable) column — set distinct signed values (unlike the computed
        // AbsChangePct, which InMemory leaves 0). A −30% drop and a +30% rise must sort to OPPOSITE ends.
        db.ProposedPrices.AddRange(
            ChgRow(1, "UP30", 30m), ChgRow(2, "UP10", 10m), ChgRow(3, "FLAT", 0m),
            ChgRow(4, "DN05", -5m), ChgRow(5, "DN30", -30m));
        db.SaveChanges();

        // Max = −20 keeps only drops of 20%+ (signed ≤ −20) — NOT the +30% rise (which |Δ| would have kept).
        Assert.Equal(new[] { "DN30" }, Skus(db, F(f => f.MaxChangePct = -20m)));
        // Min = 0 keeps only rises / flat.
        Assert.Equal(new[] { "FLAT", "UP10", "UP30" }, Skus(db, F(f => f.MinChangePct = 0m)));
        // Signed range [−10, +10].
        Assert.Equal(new[] { "DN05", "FLAT", "UP10" }, Skus(db, F(f => { f.MinChangePct = -10m; f.MaxChangePct = 10m; })));
    }

    private static ProposedPrice ChgRow(long id, string sku, decimal changePct) => new()
    {
        Id = id, PricingRunId = 1, LayerId = 1, Sku = sku, ProductName = sku, Brand = "B",
        Pptcv = 5m, CurrentPrice = 10m, ProposedPriceValue = 10m, OldPrice = 12m, AnchorPrice = 12m,
        GuardrailFlags = "", ReasonCodes = "", Status = ProposalStatus.Pending, HasChange = true,
        ChangePct = changePct,
    };

    [Fact]
    public void CostMin_ExcludesCostUnknown()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "B2", "E5" }, Skus(db, F(f => f.CostMin = 10m)));
    }

    [Fact]
    public void ProductName_Contains()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "B2" }, Skus(db, F(f => f.ProductName = "Puzzle")));
    }

    [Fact]
    public void Guardrails_Contains()
    {
        using var db = Seeded();
        Assert.Equal(new[] { "B2" }, Skus(db, F(f => f.Guardrails = "FLOOR")));
    }

    [Fact]
    public void Gj50Sync_SplitsTheSyncedRows_AndIsANoOpWhenUnset()
    {
        using var db = NewDb();
        // A GjirafaMall run stamps SYNCED_TO_GJ50 FIRST, then the adopted Gj50 decision's own reason
        // codes (see PricingRunOrchestrator.SyncedProposal) — so the token can be followed by others.
        db.ProposedPrices.AddRange(
            ReasonRow(1, "SYNC1", "SYNCED_TO_GJ50"),
            ReasonRow(2, "SYNC2", "SYNCED_TO_GJ50,ELASTIC_OPTIMAL"),
            ReasonRow(3, "OWN1", "MARGIN_TARGET"),
            ReasonRow(4, "OWN2", ""));
        db.SaveChanges();

        Assert.Equal(new[] { "OWN1", "OWN2" }, Skus(db, F(f => f.Gj50Sync = "exclude")));
        Assert.Equal(new[] { "SYNC1", "SYNC2" }, Skus(db, F(f => f.Gj50Sync = "only")));
        // Unset (and any unrecognised value) leaves the set untouched.
        Assert.Equal(4, Skus(db, F(_ => { })).Count);
    }

    private static ProposedPrice ReasonRow(long id, string sku, string reasons) => new()
    {
        Id = id, PricingRunId = 1, LayerId = 1, Sku = sku, ProductName = sku, Brand = "B",
        Pptcv = 5m, CurrentPrice = 10m, ProposedPriceValue = 9m, OldPrice = 12m, AnchorPrice = 12m,
        GuardrailFlags = "", ReasonCodes = reasons, Status = ProposalStatus.Pending, HasChange = true,
    };

    // ---------------------------------------------------------------- Snapshot-backed filters

    [Fact]
    public void Qty7Min_KeepsSellersAndExcludesNoSnapshotRow()
    {
        using var db = Seeded();
        SeedSnapshots(db);
        // qty7 ≥ 1: B2 (3) and D4 (20). A1/C3 (0) fail the bound; E5 has no snapshot row (unknown).
        Assert.Equal(new[] { "B2", "D4" }, SnapSkus(db, F(f => f.Qty7Min = 1)));
    }

    [Fact]
    public void Qty30_Range_IsInclusive()
    {
        using var db = Seeded();
        SeedSnapshots(db);
        // qty30 in [2, 10]: A1 (2) and B2 (10) inclusive; C3 (0) below, D4 (90) above, E5 unknown.
        Assert.Equal(new[] { "A1", "B2" }, SnapSkus(db, F(f => { f.Qty30Min = 2; f.Qty30Max = 10; })));
    }

    [Fact]
    public void LocalStockMax_KeepsLowStock_ExcludesUnknown()
    {
        using var db = Seeded();
        SeedSnapshots(db);
        // stock ≤ 5: A1 (0) and B2 (5). C3 (50) / D4 (200) too high; E5 has no snapshot row.
        Assert.Equal(new[] { "A1", "B2" }, SnapSkus(db, F(f => f.LocalStockMax = 5)));
    }

    [Fact]
    public void SnapshotBound_OfZero_StillExcludesNoSnapshotRow()
    {
        using var db = Seeded();
        SeedSnapshots(db);
        // A "set" bound engages the semi-join even at 0: every SKU WITH a row has stock ≥ 0, but E5
        // (no row) is an unknown value and can't match — the same rule the cost range uses.
        Assert.Equal(new[] { "A1", "B2", "C3", "D4" }, SnapSkus(db, F(f => f.LocalStockMin = 0)));
    }

    [Fact]
    public void NoSnapshotBound_IsNoOp_AndKeepsNoSnapshotRows()
    {
        using var db = Seeded();
        SeedSnapshots(db);
        // With no snapshot bound set the semi-join never engages, so E5 (no snapshot row) survives.
        Assert.Equal(new[] { "A1", "B2", "C3", "D4", "E5" }, SnapSkus(db, F(_ => { })));
    }

    // ---------------------------------------------------------------- Sort tokens

    [Theory]
    [InlineData("change_desc", "abschange", true)]
    [InlineData("change_asc", "change", false)]
    [InlineData("sku", "sku", false)]
    [InlineData("price_desc", "proposed", true)]
    [InlineData("proposed_asc", "proposed", false)]
    [InlineData("qty7_desc", "qty7", true)]
    [InlineData("", "abschange", true)]
    public void ParseSort_NormalisesLegacyAndColumnTokens(string token, string col, bool desc)
    {
        var parsed = ProposalQuery.ParseSort(token);
        Assert.Equal(col, parsed.Column);
        Assert.Equal(desc, parsed.Desc);
    }

    [Theory]
    [InlineData("qty7_desc", true)]
    [InlineData("qty30_asc", true)]
    [InlineData("localstock_desc", true)]
    [InlineData("proposed_desc", false)]
    [InlineData("prevmargin_asc", false)]
    public void IsSnapshotSort_FlagsOnlySnapshotColumns(string token, bool expected) =>
        Assert.Equal(expected, ProposalQuery.IsSnapshotSort(token));

    [Fact]
    public void ApplySort_ProposedDesc_OrdersByProposedPrice()
    {
        using var db = Seeded();
        var ordered = ProposalQuery.ApplySort(db.ProposedPrices.Where(p => p.PricingRunId == 1), "proposed_desc")
            .Select(p => p.Sku).ToList();
        Assert.Equal(new[] { "E5", "C3", "B2", "A1", "D4" }, ordered);
    }
}
