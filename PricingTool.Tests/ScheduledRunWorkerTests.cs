using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PricingTool.Core.Domain;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;
using PricingTool.Engine;

namespace PricingTool.Tests;

/// <summary>
/// The scheduler's slot bookkeeping. A run that FAILS must consume its slot — otherwise the next tick runs
/// the layer again, and again: on 2026-09-24/25 both MK layers ran ~50 times back-to-back, because the
/// failure (SqlBulkCopy rejecting one junk value) was an InvalidOperationException and the worker read that
/// type as "another run is in progress". Only a genuinely busy engine leaves the slot due.
/// </summary>
public class ScheduledRunWorkerTests
{
    /// <summary>Fails the snapshot write the way SqlBulkCopy did on prod — with an InvalidOperationException.</summary>
    private sealed class OverflowingSnapshotWriter : EfBulkWriter
    {
        public OverflowingSnapshotWriter(PricingToolDbContext db) : base(db) { }

        public override Task BulkInsertSnapshotsAsync(IReadOnlyCollection<DailySnapshot> rows, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "The given value '-1518.806034' of type SqlDecimal from the data source cannot be converted " +
                "to type decimal for Column 6 [CurrentDiscountPct] Row 63687.");
    }

    private static ServiceProvider NewServices(DbContextOptions<PricingToolDbContext> options, bool failSnapshotWrite)
    {
        var rows = new[]
        {
            new SnapshotRow { Sku = "15512795mo", OldPrice = 464m, CurrentPrice = 705190m, Pptcv = 598381.02m, SupplierWarehouseStock = 10 },
        };

        var services = new ServiceCollection();
        services.AddScoped(_ => new PricingToolDbContext(options));
        services.AddScoped<ScheduleService>();
        services.AddScoped(sp =>
        {
            var db = sp.GetRequiredService<PricingToolDbContext>();
            return OrchestratorTests.NewOrchestrator(db, rows,
                bulkWriter: failSnapshotWrite ? new OverflowingSnapshotWriter(db) : null);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A layer whose last served slot was two days ago, so it is due on the next tick. Its run time sits six
    /// hours behind the clock, so no slot boundary can fall between a test's ticks and make a re-run legitimate.
    /// </summary>
    private static (int LayerId, DateTime LastSlot) SeedDueLayer(DbContextOptions<PricingToolDbContext> options)
    {
        using var db = new PricingToolDbContext(options);
        var runAt = DateTime.UtcNow.AddHours(-6);
        var lastSlot = runAt.Date.AddDays(-2).AddHours(runAt.Hour).AddMinutes(runAt.Minute);
        var layer = new Layer
        {
            Brand = "Gjirafa50", CountryCode = "MK", DisplayName = "Gjirafa50 — North Macedonia",
            OperationalDatabase = "GjirafaEcommerce", StoreId = 1, TranslationCountryId = 3, WarehouseStoreId = 1,
            Currency = "MKD", RunTimeUtc = runAt.ToString("HH:mm", CultureInfo.InvariantCulture), CadenceHours = 24,
            IsActive = true, LastScheduledRunUtc = lastSlot,
        };
        db.Layers.Add(layer);
        db.SaveChanges();
        return (layer.Id, lastSlot);
    }

    private static DbContextOptions<PricingToolDbContext> NewOptions() =>
        new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Fact]
    public async Task A_run_that_fails_with_InvalidOperationException_consumes_its_slot_and_is_not_rerun()
    {
        var options = NewOptions();
        var (_, lastSlot) = SeedDueLayer(options);
        using var services = NewServices(options, failSnapshotWrite: true);
        var worker = new ScheduledRunWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledRunWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None);   // the next minute's tick

        using var db = new PricingToolDbContext(options);
        var run = await db.PricingRuns.SingleAsync();     // ran once, not once per tick
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("CurrentDiscountPct", run.ErrorMessage);

        var layer = await db.Layers.SingleAsync();
        Assert.True(layer.LastScheduledRunUtc > lastSlot);
        Assert.Null(ScheduleService.DueSlot(ScheduleService.ToInfo(layer), DateTime.UtcNow));
    }

    [Fact]
    public async Task A_layer_skipped_because_another_run_is_in_progress_stays_due()
    {
        var options = NewOptions();
        var (layerId, lastSlot) = SeedDueLayer(options);
        using (var seed = new PricingToolDbContext(options))
        {
            seed.PricingRuns.Add(new PricingRun { LayerId = layerId, StartedUtc = DateTime.UtcNow, Status = RunStatus.Running });
            seed.SaveChanges();
        }
        using var services = NewServices(options, failSnapshotWrite: false);
        var worker = new ScheduledRunWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledRunWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);

        using var db = new PricingToolDbContext(options);
        Assert.Equal(1, await db.PricingRuns.CountAsync());   // only the in-flight run; nothing new started
        var layer = await db.Layers.SingleAsync();
        Assert.Equal(lastSlot, layer.LastScheduledRunUtc);
        Assert.NotNull(ScheduleService.DueSlot(ScheduleService.ToInfo(layer), DateTime.UtcNow));
    }
}
