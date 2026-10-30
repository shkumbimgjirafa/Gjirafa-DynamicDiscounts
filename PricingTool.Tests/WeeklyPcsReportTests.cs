using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PricingTool.Data;
using PricingTool.Engine;
using Act = PricingTool.Engine.WeeklyPcsReportWorker.ReportAction;

namespace PricingTool.Tests;

/// <summary>
/// The weekly pcs/gjirafa50 email: sent once per week on Monday from 09:00 local, caught up later in the week
/// if the pod was down, never re-sent after a restart, and on first start only a setup check to the sender.
/// </summary>
public class WeeklyPcsReportTests
{
    private static readonly TimeOnly NineAm = new(9, 0);
    private static readonly DateOnly Mon26Oct = new(2026, 10, 26);
    private static readonly DateOnly Mon19Oct = new(2026, 10, 19);

    [Theory]
    [InlineData("2026-10-26 08:59", Act.Wait)]   // Monday, before send time
    [InlineData("2026-10-26 09:00", Act.Send)]   // Monday, on time
    [InlineData("2026-10-29 14:00", Act.Send)]   // pod was down Monday: catch up on Thursday
    [InlineData("2026-11-01 23:59", Act.Send)]   // ...even on Sunday night
    public void Due_from_monday_nine_until_sent(string localNow, Act expected)
    {
        Assert.Equal(expected, WeeklyPcsReportWorker.Decide(DateTime.Parse(localNow), Mon19Oct, NineAm));
    }

    [Fact]
    public void Already_sent_this_week_waits_for_next_monday()
    {
        Assert.Equal(Act.Wait, WeeklyPcsReportWorker.Decide(new DateTime(2026, 10, 26, 9, 5, 0), Mon26Oct, NineAm));
        Assert.Equal(Act.Wait, WeeklyPcsReportWorker.Decide(new DateTime(2026, 11, 1, 23, 0, 0), Mon26Oct, NineAm));
        Assert.Equal(Act.Send, WeeklyPcsReportWorker.Decide(new DateTime(2026, 11, 2, 9, 0, 0), Mon26Oct, NineAm));
    }

    [Fact]
    public void First_start_is_a_setup_check()
    {
        Assert.Equal(Act.SetupCheck, WeeklyPcsReportWorker.Decide(new DateTime(2026, 10, 23, 15, 0, 0), null, NineAm));
    }

    [Theory]
    [InlineData("2026-10-26", "2026-10-26")]   // Monday
    [InlineData("2026-10-25", "2026-10-19")]   // Sunday belongs to the week before
    [InlineData("2026-10-23", "2026-10-19")]   // Friday
    public void Week_starts_on_monday(string day, string monday)
    {
        Assert.Equal(DateOnly.Parse(monday), WeeklyPcsReportWorker.WeekStart(DateTime.Parse(day)));
    }

    [Fact]
    public void Local_time_follows_cet_and_cest()
    {
        // 07:00 UTC is 09:00 in summer time and 08:00 after the clocks go back on 25 Oct 2026.
        Assert.Equal(9, WeeklyPcsReport.ToLocal(new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc), "Europe/Belgrade").Hour);
        Assert.Equal(8, WeeklyPcsReport.ToLocal(new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc), "Europe/Belgrade").Hour);
    }

    [Fact]
    public void Csv_quotes_every_field_and_keeps_column_order()
    {
        var csv = WeeklyPcsReport.ToCsv(new[] { "sku", "sifra", "pro" },
            new[] { new object?[] { "1app", "say \"hi\"", 3 }, new object?[] { "2app", null, 0 } });

        Assert.Equal(Encoding.UTF8.GetPreamble(), csv.Take(3));
        Assert.Equal("\"sku\",\"sifra\",\"pro\"\r\n\"1app\",\"say \"\"hi\"\"\",\"3\"\r\n\"2app\",\"\",\"0\"\r\n",
            Encoding.UTF8.GetString(csv.Skip(3).ToArray()));
    }

    [Fact]
    public void Summary_uses_pro_and_lag_only_when_present()
    {
        var rows = new[]
        {
            new object?[] { "a", 2, 0 },   // sold, no stock left
            new object?[] { "b", 1, 5 },
            new object?[] { "c", 0, 7 },
        };
        var full = WeeklyPcsReport.Summarize(new[] { "sku", "pro", "lag" }, rows);
        Assert.Contains("Units sold (pro):        3 on 2 SKUs", full);
        Assert.Contains("Units in stock (lag):    12", full);
        Assert.Contains("Sold but no stock left:  1 SKUs", full);

        var bare = WeeklyPcsReport.Summarize(new[] { "sku", "x", "y" }, rows);
        Assert.Equal(new[] { "Rows in the file:        3" }, bare);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("$(WeeklyPcsReportSmtpPassword)", false)]   // pipeline variable never defined
    [InlineData("__WEEKLY_PCS_REPORT_SMTP_PASSWORD__", false)]   // manifest placeholder not replaced
    [InlineData("abcdefghijklmnop", true)]
    public void Unset_password_keeps_the_report_off(string password, bool configured)
    {
        var o = new WeeklyPcsReportOptions { Enabled = true, To = "a@b.c", SmtpUser = "me@b.c", SmtpPassword = password };
        Assert.Equal(configured, o.IsConfigured);
    }

    [Fact]
    public void App_password_pasted_with_google_spacing_is_normalized_and_described_without_its_value()
    {
        Assert.Equal("abcdefghijklmnop", WeeklyPcsReport.NormalizePassword(" abcd efgh ijkl mnop\n"));

        var shape = WeeklyPcsReport.DescribePassword("abcd efgh ijkl mnop");
        Assert.Contains("19 chars as set, 3 whitespace", shape);
        Assert.Contains("16 lowercase letters (app-password shape)", shape);
        Assert.DoesNotContain("abcd", shape);

        Assert.Contains("NOT 16 lowercase letters", WeeklyPcsReport.DescribePassword("MyN0rmal!Password"));
    }

    /// <summary>Starts the worker for its first tick and returns the recorded ToolSettings, keyed.</summary>
    private static async Task<Dictionary<string, string>> RunFirstTick(WeeklyPcsReportOptions opts)
    {
        var dbOptions = new DbContextOptionsBuilder<PricingToolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new PricingToolDbContext(dbOptions));
        using var provider = services.BuildServiceProvider();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SourceReadOnly"] = "Server=unused" })
            .Build();
        var worker = new WeeklyPcsReportWorker(provider.GetRequiredService<IServiceScopeFactory>(), config,
            Options.Create(opts), NullLogger<WeeklyPcsReportWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100; i++)
        {
            await using var probe = new PricingToolDbContext(dbOptions);
            if (await probe.ToolSettings.AnyAsync(s => s.Key == WeeklyPcsReportWorker.StatusKey)) break;
            await Task.Delay(50);
        }
        await worker.StopAsync(CancellationToken.None);

        await using var db = new PricingToolDbContext(dbOptions);
        return await db.ToolSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    [Fact]
    public async Task Unset_password_is_recorded_as_off()
    {
        var settings = await RunFirstTick(new WeeklyPcsReportOptions
        {
            Enabled = true, To = "a@b.c", SmtpUser = "me@b.c", SmtpPassword = "__WEEKLY_PCS_REPORT_SMTP_PASSWORD__",
        });

        Assert.Contains("off:", settings[WeeklyPcsReportWorker.StatusKey]);
        Assert.Contains("placeholder", settings[WeeklyPcsReportWorker.StatusKey]);
        Assert.DoesNotContain(WeeklyPcsReportWorker.LastSentWeekKey, settings.Keys);
    }

    [Fact]
    public async Task A_failure_is_recorded_with_its_stage_and_the_week_stays_unsent()
    {
        var settings = await RunFirstTick(new WeeklyPcsReportOptions
        {
            Enabled = true, To = "a@b.c", SmtpUser = "me@b.c", SmtpPassword = "abcdefghijklmnop",
            SqlFile = "Reports/does-not-exist.sql",
        });

        var status = settings[WeeklyPcsReportWorker.StatusKey];
        Assert.Contains("error at query", status);
        Assert.Contains("FileNotFoundException", status);
        Assert.DoesNotContain("abcdefghijklmnop", status);
        Assert.DoesNotContain(WeeklyPcsReportWorker.LastSentWeekKey, settings.Keys);
    }
}
