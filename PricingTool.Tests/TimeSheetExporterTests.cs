using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent;
using PricingTool.Agent.Operations;
using PricingTool.Agent.Storage;

namespace PricingTool.Tests;

/// <summary>The organisers' hours-saved sheet: only rows from its period with a task type's minutes before are exported.</summary>
public class TimeSheetExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pt-timesheet-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Rows_from_before_the_period_and_rows_without_minutes_before_are_kept_but_not_exported()
    {
        await using var db = new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.Database.EnsureCreatedAsync();
        (await db.TimeSheetTaskTypes.SingleAsync(t => t.Id == AgentDbContext.TaskTypes.PrePublishRunCheck)).MinutesBefore = 120;
        TimeSheetEntry Row(DateOnly date, int type, string what) =>
            new() { Date = date, UserEmail = "owner@example.test", TaskTypeId = type, What = what, MinutesAfter = 1.5m, Proof = "test" };
        db.TimeSheetEntries.AddRange(
            Row(TimeSheetExporter.PeriodStart.AddDays(-1), AgentDbContext.TaskTypes.PrePublishRunCheck, "the build weekend"),
            Row(TimeSheetExporter.PeriodStart, AgentDbContext.TaskTypes.PrePublishRunCheck, "first day of the period"),
            Row(TimeSheetExporter.PeriodStart, AgentDbContext.TaskTypes.CreateAndPushDiscounts, "no minutes before yet"));
        await db.SaveChangesAsync();

        var r = await new TimeSheetExporter(db, Options.Create(new AgentOptions { Cycle = new CycleOptions { TimeSheetDirectory = _dir } })).ExportAsync();

        Assert.Equal((1, 1, 1), (r.Exported, r.WithoutMinutesBefore, r.BeforePeriod));
        var csv = await File.ReadAllLinesAsync(r.CsvPath);
        Assert.Equal(2, csv.Length);                                                            // the header and the one row
        Assert.Contains("first day of the period,120,1.5,118.5", csv[1]);
    }
}
