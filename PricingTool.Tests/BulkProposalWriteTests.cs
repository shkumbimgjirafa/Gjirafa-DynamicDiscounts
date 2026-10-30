using System.Data;
using PricingTool.Core.Domain;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Tests;

/// <summary>
/// Guards the production proposal write path. Every other test drives an EF-backed
/// <c>IBulkWriteService</c> double, so the real <see cref="SqlBulkCopy"/> DataTable — which lists its
/// columns by hand — is invisible to them: a property added to <see cref="ProposedPrice"/> but not to
/// the builder is written as its CLR default on every production run, with the whole suite green.
/// That is exactly how ProposedPrice.IsNewProduct shipped reading false for every row.
/// </summary>
public class BulkProposalWriteTests
{
    /// <summary>
    /// Columns the insert deliberately leaves to their DB default because nothing sets them at run
    /// time — the manual-override trio is written later by the Proposals review screen, and
    /// AbsChangePct is a DB-computed column (SqlBulkCopy cannot write one).
    /// </summary>
    private static readonly HashSet<string> NotWrittenAtInsert = new()
    {
        nameof(ProposedPrice.EngineProposedPrice),
        nameof(ProposedPrice.ManualOverrideBy),
        nameof(ProposedPrice.ManualOverrideUtc),
        nameof(ProposedPrice.AbsChangePct),
    };

    [Fact]
    public void BuildProposalTable_covers_every_persisted_property()
    {
        var table = BulkWriteService.BuildProposalTable(new[] { new ProposedPrice { Sku = "X" } });
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToHashSet();

        var missing = typeof(ProposedPrice).GetProperties()
            .Where(p => p.CanWrite || NotWrittenAtInsert.Contains(p.Name))
            .Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Where(name => !NotWrittenAtInsert.Contains(name) && !columns.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            $"ProposedPrice properties missing from the bulk-copy column list (they would silently " +
            $"persist as their CLR default): {string.Join(", ", missing)}");
    }

    [Fact]
    public void BuildProposalTable_carries_the_new_product_flag()
    {
        var table = BulkWriteService.BuildProposalTable(new[]
        {
            new ProposedPrice { Id = 1, Sku = "NEW-1", IsNewProduct = true, DeadStockStage = DeadStockStage.None },
            new ProposedPrice { Id = 2, Sku = "OLD-1", IsNewProduct = false, DeadStockStage = DeadStockStage.None },
        });

        Assert.True((bool)table.Rows[0]["IsNewProduct"]);
        Assert.False((bool)table.Rows[1]["IsNewProduct"]);
    }
}
