using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Operations;

public sealed record TimeSheetExport(string XlsxPath, string CsvPath, string JsonPath, int Exported, int WithoutMinutesBefore, int BeforePeriod = 0);

/// <summary>
/// The organisers' hours-saved sheet, filled by the app from its own <c>ai.TimeSheetEntry</c> rows: a copy of
/// the template (columns A–H from row 7; the example row is replaced, column F keeps its formula), plus CSV and
/// JSON for Jarvis. A row whose task type has no measured "minutes before" yet is kept in the database but not
/// exported (the user sets it once per task type), and so is a row from before the sheet's period.
/// </summary>
public sealed class TimeSheetExporter
{
    public const string TemplateFile = "Time-sheet-template-hours-saved.xlsx";
    private const int FirstRow = 7;

    /// <summary>The first day the organisers' sheet covers (its template: one row per real task, 2 Nov – 2 Dec). The
    /// build weekend's runs before it stay in the database but are not exported (the user's decision, 1 Nov).</summary>
    public static readonly DateOnly PeriodStart = new(2026, 11, 2);

    private readonly AgentDbContext _db;
    private readonly CycleOptions _options;
    private readonly TimeProvider _clock;

    public TimeSheetExporter(AgentDbContext db, IOptions<AgentOptions> options, TimeProvider? clock = null)
    {
        _db = db;
        _options = options.Value.Cycle;
        _clock = clock ?? TimeProvider.System;
    }

    public static string TemplatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Templates", TemplateFile);

    public async Task<TimeSheetExport> ExportAsync(CancellationToken ct = default)
    {
        var types = await _db.TimeSheetTaskTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var all = await _db.TimeSheetEntries.AsNoTracking().OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync(ct);
        var inPeriod = all.Where(e => e.Date >= PeriodStart).ToList();
        // The task type's current "minutes before" applies to every row of that type once the user has set it.
        var rows = inPeriod.Select(e => (Entry: e, Before: e.MinutesBefore ?? types.GetValueOrDefault(e.TaskTypeId)?.MinutesBefore))
            .Where(r => r.Before is not null).ToList();

        var dir = _options.TimeSheetDir();
        Directory.CreateDirectory(dir);
        var stamp = _clock.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var xlsx = Path.Combine(dir, $"time-sheet-{stamp}.xlsx");
        var csv = Path.Combine(dir, $"time-sheet-{stamp}.csv");
        var json = Path.Combine(dir, $"time-sheet-{stamp}.json");

        using (var wb = new XLWorkbook(TemplatePath))
        {
            var ws = wb.Worksheet(1);
            ws.Range(FirstRow, 1, FirstRow, 5).Clear(XLClearOptions.Contents);
            ws.Range(FirstRow, 7, FirstRow, 8).Clear(XLClearOptions.Contents);
            var row = FirstRow;
            foreach (var (e, before) in rows)
            {
                ws.Cell(row, 1).Value = e.Date.ToDateTime(TimeOnly.MinValue);
                ws.Cell(row, 2).Value = e.UserEmail;
                ws.Cell(row, 3).Value = e.What;
                ws.Cell(row, 4).Value = before!.Value;
                ws.Cell(row, 5).Value = e.MinutesAfter;
                if (ws.Cell(row, 6).FormulaA1.Length == 0)
                    ws.Cell(row, 6).FormulaA1 = $"IF(AND(ISNUMBER(D{row}),ISNUMBER(E{row})),MAX(0,D{row}-E{row}),\"\")";
                ws.Cell(row, 7).Value = e.Proof;
                ws.Cell(row, 8).Value = e.Comment ?? "";
                row++;
            }
            wb.SaveAs(xlsx);
        }

        var sb = new StringBuilder("Date,Who,What,MinutesBefore,MinutesAfter,MinutesSaved,Proof,Comment\n");
        foreach (var (e, before) in rows)
            sb.Append(string.Join(",", e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Csv(e.UserEmail), Csv(e.What),
                before!.Value.ToString(CultureInfo.InvariantCulture), e.MinutesAfter.ToString(CultureInfo.InvariantCulture),
                Math.Max(0, before.Value - e.MinutesAfter).ToString(CultureInfo.InvariantCulture), Csv(e.Proof), Csv(e.Comment ?? ""))).Append('\n');
        await File.WriteAllTextAsync(csv, sb.ToString(), Encoding.UTF8, ct);
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(rows.Select(r => new
        {
            date = r.Entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), who = r.Entry.UserEmail, what = r.Entry.What,
            minutesBefore = r.Before, minutesAfter = r.Entry.MinutesAfter, minutesSaved = Math.Max(0, r.Before!.Value - r.Entry.MinutesAfter),
            proof = r.Entry.Proof, comment = r.Entry.Comment, source = r.Entry.Source,
        }), new JsonSerializerOptions { WriteIndented = true }), ct);

        return new TimeSheetExport(xlsx, csv, json, rows.Count, inPeriod.Count - rows.Count, all.Count - inPeriod.Count);
    }

    private static string Csv(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
