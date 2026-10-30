using System.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Guards the production write path against values its bounded decimal columns can't hold. SqlBulkCopy
/// rejects such a value by failing the WHOLE copy, so one junk source price used to cost a layer its
/// entire run: on 2026-09-24/25 SKU 15512795mo's 464 MKD shelf price, against a 705,190 MKD price, made
/// CurrentDiscountPct −1518.8 for a decimal(9,6) column and every MK run failed at the snapshot write.
/// The EF-backed test double never touches these DataTables, so they are asserted here directly.
/// </summary>
public class BulkWriteColumnRangeTests
{
    private static DailySnapshot Snapshot(string sku, decimal? discount = null, decimal? grossMargin = null) => new()
    {
        LayerId = 2, SnapshotDate = new DateTime(2026, 9, 25),
        Sku = sku, CurrentDiscountPct = discount, GrossMargin = grossMargin,
    };

    [Fact]
    public void Snapshot_discount_that_cannot_fit_its_column_is_written_as_null()
    {
        var table = BulkWriteService.BuildSnapshotTable(new[]
        {
            // The live row: (Old − Current) / Old with a junk shelf price.
            Snapshot("15512795mo", discount: (464m - 705190m) / 464m),
            Snapshot("NORMAL", discount: 0.254321m),
        });

        Assert.Equal(DBNull.Value, table.Rows[0]["CurrentDiscountPct"]);
        Assert.Equal(0.254321m, table.Rows[1]["CurrentDiscountPct"]);
    }

    [Fact]
    public void Snapshot_discount_keeps_every_value_the_column_can_hold()
    {
        var table = BulkWriteService.BuildSnapshotTable(new[]
        {
            Snapshot("EDGE-HIGH", discount: 999.999999m),
            Snapshot("EDGE-LOW", discount: -999.999999m),
            Snapshot("ROUNDS-OVER", discount: 999.9999996m),   // rounds to 1000.000000 — one step past the column
        });

        Assert.Equal(999.999999m, table.Rows[0]["CurrentDiscountPct"]);
        Assert.Equal(-999.999999m, table.Rows[1]["CurrentDiscountPct"]);
        Assert.Equal(DBNull.Value, table.Rows[2]["CurrentDiscountPct"]);
    }

    [Fact]
    public void Snapshot_gross_margin_that_cannot_fit_its_column_is_written_as_null()
    {
        var table = BulkWriteService.BuildSnapshotTable(new[]
        {
            Snapshot("AL-LIVE", grossMargin: -11396.3846m),     // seen on the AL layers — fits decimal(9,4)
            Snapshot("JUNK", grossMargin: -123456.7890m),        // cost ~1,200× price — doesn't
        });

        Assert.Equal(-11396.3846m, table.Rows[0]["GrossMargin"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["GrossMargin"]);
    }

    [Fact]
    public void Proposal_change_pct_is_clamped_to_its_column_rather_than_failing_the_copy()
    {
        var table = BulkWriteService.BuildProposalTable(new[]
        {
            new ProposedPrice { Id = 1, Sku = "UP", ChangePct = 250000m },
            new ProposedPrice { Id = 2, Sku = "DOWN", ChangePct = -250000m },
            new ProposedPrice { Id = 3, Sku = "NORMAL", ChangePct = 12.34567m },
        });

        Assert.Equal(99999.9999m, table.Rows[0]["ChangePct"]);
        Assert.Equal(-99999.9999m, table.Rows[1]["ChangePct"]);
        Assert.Equal(12.3457m, table.Rows[2]["ChangePct"]);
    }
}
