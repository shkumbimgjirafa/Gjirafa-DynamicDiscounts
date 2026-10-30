using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Data;
using PricingTool.Data.Entities;

namespace PricingTool.Engine;

/// <summary>
/// Settings for the weekly pcs/gjirafa50 sales + stock email (section "WeeklyPcsReport"). Not part of the
/// pricing logic: a standalone report the engine pod sends because it is always on and already reads the
/// source server. The SMTP password is a Gmail app password injected at deploy time (never committed).
/// </summary>
public class WeeklyPcsReportOptions
{
    public const string Section = "WeeklyPcsReport";

    public bool Enabled { get; set; }
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    /// <summary>Local send time on Mondays, "HH:mm" in <see cref="TimeZone"/>.</summary>
    public string SendAtLocal { get; set; } = "09:00";
    public string TimeZone { get; set; } = "Europe/Belgrade";
    /// <summary>Query file, relative to the app directory. Its final SELECT is the CSV.</summary>
    public string SqlFile { get; set; } = "Reports/pcs-gjirafa50-weekly.sql";

    /// <summary>
    /// True when the report can actually be sent. An unset pipeline secret arrives as the literal
    /// "$(Name)" or as the manifest placeholder "__NAME__" — both count as missing.
    /// </summary>
    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(To) && !string.IsNullOrWhiteSpace(SmtpUser) &&
        !string.IsNullOrWhiteSpace(SmtpPassword) &&
        !SmtpPassword.StartsWith("$(", StringComparison.Ordinal) &&
        !SmtpPassword.StartsWith("__", StringComparison.Ordinal);
}

/// <summary>
/// Emails the weekly pcs/gjirafa50 report every Monday at 09:00 local time (catching up later in the week if
/// the pod was down then). The last-sent week is kept in ToolSettings so a pod restart never re-sends. On the
/// very first start (no state yet) it sends a "[setup check]" copy to the sender only — proving the pod can
/// reach the source DB and Gmail — and marks the current week done, so the first real email goes out the
/// following Monday instead of on deploy.
/// </summary>
public class WeeklyPcsReportWorker : BackgroundService
{
    internal const string LastSentWeekKey = "WeeklyPcsReport.LastSentWeek";
    internal const string StatusKey = "WeeklyPcsReport.Status";
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly WeeklyPcsReportOptions _options;
    private readonly ILogger<WeeklyPcsReportWorker> _logger;
    private bool _smtpProbed;

    public WeeklyPcsReportWorker(IServiceScopeFactory scopeFactory, IConfiguration config,
        IOptions<WeeklyPcsReportOptions> options, ILogger<WeeklyPcsReportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _options = options.Value;
        _logger = logger;
    }

    public enum ReportAction { Wait, SetupCheck, Send }

    /// <summary>What to do now, given the local clock and the Monday of the last week already handled.</summary>
    internal static ReportAction Decide(DateTime localNow, DateOnly? lastSentWeek, TimeOnly sendAt)
    {
        var thisMonday = WeekStart(localNow);
        if (lastSentWeek is null) return ReportAction.SetupCheck;
        if (lastSentWeek.Value >= thisMonday) return ReportAction.Wait;
        return localNow >= thisMonday.ToDateTime(sendAt) ? ReportAction.Send : ReportAction.Wait;
    }

    internal static DateOnly WeekStart(DateTime local)
    {
        var d = DateOnly.FromDateTime(local);
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured)
        {
            var o = _options;
            var password = string.IsNullOrWhiteSpace(o.SmtpPassword) ? "empty"
                : o.SmtpPassword.StartsWith("$(", StringComparison.Ordinal) || o.SmtpPassword.StartsWith("__", StringComparison.Ordinal)
                    ? "placeholder (pipeline variable not injected)" : "set";
            await WriteStatusAsync($"off: Enabled={o.Enabled}, To set={o.To.Length > 0}, SmtpUser set={o.SmtpUser.Length > 0}, password {password}", stoppingToken);
            _logger.LogInformation("Weekly pcs/gjirafa50 report is off (WeeklyPcsReport not enabled or SMTP password not set).");
            return;
        }
        var sendAt = TimeOnly.ParseExact(_options.SendAtLocal, "HH:mm", CultureInfo.InvariantCulture);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = Tick;
            var stage = "state";
            try
            {
                var localNow = WeeklyPcsReport.ToLocal(DateTime.UtcNow, _options.TimeZone);
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
                var row = await db.ToolSettings.FirstOrDefaultAsync(s => s.Key == LastSentWeekKey, stoppingToken);
                DateOnly? lastSent = row is null ? null : DateOnly.ParseExact(row.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

                var action = Decide(localNow, lastSent, sendAt);
                if (action != ReportAction.Wait)
                {
                    var setupCheck = action == ReportAction.SetupCheck;
                    stage = "query";
                    var report = await WeeklyPcsReport.BuildAsync(_config, _options, localNow, stoppingToken);
                    stage = "smtp";
                    if (setupCheck)
                        await WeeklyPcsReport.SendAsync(_options, report, _options.SmtpUser, "", "[setup check] ", stoppingToken);
                    else
                        await WeeklyPcsReport.SendAsync(_options, report, _options.To, _options.Cc, "", stoppingToken);

                    // A setup check before this Monday's send time leaves today's real email still due.
                    stage = "save";
                    var thisMonday = WeekStart(localNow);
                    var handled = localNow >= thisMonday.ToDateTime(sendAt) ? thisMonday : thisMonday.AddDays(-7);
                    if (row is null)
                        db.ToolSettings.Add(row = new ToolSetting { Key = LastSentWeekKey });
                    row.Value = handled.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    row.UpdatedUtc = DateTime.UtcNow;
                    row.UpdatedBy = "engine";
                    await db.SaveChangesAsync(stoppingToken);

                    var to = setupCheck ? _options.SmtpUser : _options.To;
                    await WriteStatusAsync($"{(setupCheck ? "setup check" : "report")} sent for {report.Label}: {report.RowCount} rows -> {to}", stoppingToken);
                    _logger.LogInformation("Weekly pcs/gjirafa50 report {Kind} for {Label}: {Rows} rows -> {To}.",
                        setupCheck ? "setup check sent" : "sent", report.Label, report.RowCount, to);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Weekly pcs/gjirafa50 report failed at {Stage}; retrying in 30 minutes.", stage);
                var chain = new List<string>();
                for (var e = ex; e is not null; e = e.InnerException) chain.Add($"{e.GetType().Name}: {e.Message}");
                var status = $"error at {stage}: {string.Join(" <- ", chain)}";

                // SmtpClient hides the server's reply (a rejected login surfaces as "connection was closed"), so on
                // the first SMTP failure per process, replay the login by hand once and record what the server said.
                if (stage == "smtp" && !_smtpProbed)
                {
                    _smtpProbed = true;
                    status += $" | password: {WeeklyPcsReport.DescribePassword(_options.SmtpPassword)}" +
                              $" | AUTH reply: {await WeeklyPcsReport.ProbeSmtpAuthAsync(_options, stoppingToken)}";
                }
                await WriteStatusAsync(status, stoppingToken);
                wait = RetryAfterFailure;
            }

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Records the latest outcome in ToolSettings ("WeeklyPcsReport.Status") so it can be read from the DB
    /// without pod-log access. Best effort: a failure here is only logged.
    /// </summary>
    private async Task WriteStatusAsync(string status, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
            var row = await db.ToolSettings.FirstOrDefaultAsync(s => s.Key == StatusKey, ct);
            if (row is null) db.ToolSettings.Add(row = new ToolSetting { Key = StatusKey });
            var value = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC {status}";
            row.Value = value.Length > 1024 ? value[..1024] : value;
            row.UpdatedUtc = DateTime.UtcNow;
            row.UpdatedBy = "engine";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record the weekly report status.");
        }
    }
}

/// <summary>Builds and sends the report: runs the query, renders the CSV and the summary lines.</summary>
public static class WeeklyPcsReport
{
    public sealed record Result(string Label, int RowCount, IReadOnlyList<string> Columns,
        IReadOnlyList<string> Summary, byte[] Csv);

    /// <summary>UTC to the report's local time; falls back to the EU CET/CEST rule if the zone is unknown.</summary>
    public static DateTime ToLocal(DateTime utc, string timeZone)
    {
        foreach (var id in new[] { timeZone, "Europe/Belgrade", "Central European Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

        DateTime LastSunday(int month)
        {
            var d = new DateTime(utc.Year, month, DateTime.DaysInMonth(utc.Year, month), 1, 0, 0, DateTimeKind.Utc);
            return d.AddDays(-(int)d.DayOfWeek);
        }
        return utc.AddHours(utc >= LastSunday(3) && utc < LastSunday(10) ? 2 : 1);
    }

    public static async Task<Result> BuildAsync(IConfiguration config, WeeklyPcsReportOptions options,
        DateTime localNow, CancellationToken ct)
    {
        var connectionString = config.GetConnectionString("SourceReadOnly")
            ?? throw new InvalidOperationException("Missing connection string 'SourceReadOnly'.");
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, options.SqlFile), ct);

        var columns = new List<string>();
        var rows = new List<object?[]>();
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SET NOCOUNT ON;\n" + sql, conn) { CommandTimeout = 600 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            while (await reader.ReadAsync(ct))
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }
        }

        // Same week the SQL reports: the Monday-to-Sunday before this week's Monday.
        var thisMonday = WeeklyPcsReportWorker.WeekStart(localNow);
        var label = $"{thisMonday.AddDays(-7):yyyy-MM-dd} to {thisMonday.AddDays(-1):yyyy-MM-dd}";
        return new Result(label, rows.Count, columns, Summarize(columns, rows), ToCsv(columns, rows));
    }

    /// <summary>Summary lines for the email; pro (units sold) / lag (units in stock) lines only when present.</summary>
    internal static List<string> Summarize(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        int pro = Index(columns, "pro"), lag = Index(columns, "lag");
        decimal Num(object?[] r, int i) => r[i] is null ? 0m : Convert.ToDecimal(r[i], CultureInfo.InvariantCulture);

        var lines = new List<string> { $"Rows in the file:        {rows.Count}" };
        if (pro >= 0)
            lines.Add($"Units sold (pro):        {rows.Sum(r => Num(r, pro)):0.##} on {rows.Count(r => Num(r, pro) > 0)} SKUs");
        if (lag >= 0)
            lines.Add($"Units in stock (lag):    {rows.Sum(r => Num(r, lag)):0.##}");
        if (pro >= 0 && lag >= 0)
            lines.Add($"Sold but no stock left:  {rows.Count(r => Num(r, pro) > 0 && Num(r, lag) == 0)} SKUs");
        return lines;
    }

    /// <summary>UTF-8 (with BOM, so Excel reads it right) CSV; every field quoted, quotes doubled.</summary>
    internal static byte[] ToCsv(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        static string Q(object? v) =>
            "\"" + (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder();
        sb.Append(string.Join(",", columns.Select(Q))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", r.Select(Q))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static async Task SendAsync(WeeklyPcsReportOptions options, Result report, string to, string cc,
        string subjectPrefix, CancellationToken ct)
    {
        using var msg = new MailMessage
        {
            From = new MailAddress(options.SmtpUser),
            Subject = $"{subjectPrefix}Appcom Report - weekly sales and stock ({report.Label})",
            Body =
                $"Weekly sales and stock for pcs/gjirafa50, week {report.Label} (Mon-Sun).\r\n\r\n" +
                string.Join("\r\n", report.Summary) + "\r\n\r\n" +
                $"The CSV is attached ({string.Join(", ", report.Columns)}).\r\n" +
                "This email is sent automatically every Monday at 09:00.\r\n",
        };
        foreach (var a in Split(to)) msg.To.Add(a);
        foreach (var a in Split(cc)) msg.CC.Add(a);
        msg.Attachments.Add(new Attachment(new MemoryStream(report.Csv),
            $"pcs-gjirafa50 sales and stock {report.Label}.csv", "text/csv"));

        using var smtp = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(options.SmtpUser, NormalizePassword(options.SmtpPassword)),
        };
        // A firewall that silently drops the connection would otherwise hang the send for minutes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await smtp.SendMailAsync(msg, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP send to {options.SmtpHost}:{options.SmtpPort} timed out after 2 minutes (outbound port blocked?).");
        }
    }

    /// <summary>
    /// Google shows an app password as four groups of four letters; the real password has no spaces, so any
    /// whitespace pasted along with it is dropped.
    /// </summary>
    internal static string NormalizePassword(string password) =>
        new(password.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>The password's shape for diagnostics — never its value.</summary>
    internal static string DescribePassword(string password)
    {
        var p = NormalizePassword(password);
        var shape = p.Length == 16 && p.All(c => c is >= 'a' and <= 'z') ? "16 lowercase letters (app-password shape)" : "NOT 16 lowercase letters";
        return $"{password.Length} chars as set, {password.Count(char.IsWhiteSpace)} whitespace; after trimming {p.Length} chars, " +
               $"{shape}, {p.Count(char.IsUpper)} uppercase, {p.Count(char.IsDigit)} digits";
    }

    /// <summary>
    /// Replays the SMTP login by hand (EHLO, STARTTLS, AUTH PLAIN) and returns the server's reply to AUTH, e.g.
    /// "535-5.7.8 Username and Password not accepted" or "235 2.7.0 Accepted". The reply never echoes the password.
    /// </summary>
    internal static async Task<string> ProbeSmtpAuthAsync(WeeklyPcsReportOptions options, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(options.SmtpHost, options.SmtpPort, cts.Token);
            Stream stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);

            async Task Send(string line)
            {
                var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
                await stream.WriteAsync(bytes, cts.Token);
            }
            async Task<string> Reply()
            {
                var lines = new List<string>();
                string line;
                do { line = await reader.ReadLineAsync(cts.Token) ?? ""; lines.Add(line); }
                while (line.Length > 3 && line[3] == '-');
                return string.Join(" ", lines);
            }

            await Reply();
            await Send("EHLO engine"); await Reply();
            await Send("STARTTLS");
            var tls = await Reply();
            if (!tls.StartsWith("220", StringComparison.Ordinal)) return "STARTTLS refused: " + tls;

            var ssl = new System.Net.Security.SslStream(stream);
            await ssl.AuthenticateAsClientAsync(options.SmtpHost);
            stream = ssl;
            reader = new StreamReader(ssl, Encoding.ASCII);
            await Send("EHLO engine"); await Reply();

            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "\0" + options.SmtpUser + "\0" + NormalizePassword(options.SmtpPassword)));
            await Send("AUTH PLAIN " + token);
            var auth = await Reply();
            await Send("QUIT");
            return auth;
        }
        catch (Exception ex)
        {
            return $"probe failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static IEnumerable<string> Split(string addresses) =>
        addresses.Split(',', ';').Select(a => a.Trim()).Where(a => a.Length > 0);

    private static int Index(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
