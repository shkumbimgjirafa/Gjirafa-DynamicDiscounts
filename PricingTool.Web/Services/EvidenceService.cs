using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PricingTool.Agent.Tools;

namespace PricingTool.Web.Services;

/// <summary>Where the Evidence page reads numbers.json (empty: found above the content root) and the database user its live
/// recomputes run as.</summary>
public sealed class EvidenceOptions
{
    public const string SectionName = "Evidence";
    public string Directory { get; set; } = "";
    public string ReaderUser { get; set; } = "evidence_reader";
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>One compared cell of a recompute: a result set, a column, the row whose columns equal <see cref="Where"/> (or the
/// sum over them), and the value build-numbers.ps1 stored from the same file.</summary>
public sealed record EvidenceCheck(string Label, int Set, string Column, IReadOnlyDictionary<string, string>? Where, string? Agg, decimal? Value);

public sealed record EvidenceRecompute(string File, IReadOnlyList<EvidenceCheck> Checks);

/// <summary>A figure of numbers.json as it is stored: nothing on the page is typed by hand.</summary>
public sealed record EvidenceFigure(string Id, string Name, JsonElement Value, string Unit, JsonElement Label, string Method, string Source, string Script,
    EvidenceRecompute? Recompute, string? SinceRegistration);

public sealed record EvidencePage(string Generated, string Note, IReadOnlyList<EvidenceFigure> Figures,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Uses, IReadOnlyDictionary<string, DateTime> Loads, string? LoadsError);

public sealed record RecomputeLine(string FigureId, string Label, decimal? Stored, decimal? Live)
{
    public bool Matches => Stored == Live;
}

public sealed record RecomputeResult(string File, long Ms, IReadOnlyList<RecomputeLine> Lines);

/// <summary>
/// The Evidence page (plan section 5; Prompt 11): every figure of <c>docs/hackathon-2026/evidence/numbers.json</c> with its
/// label, method, sources and their load times (<c>hk.DataSources</c>), and where it is used (the one-pager, the financial
/// sheet's cells, HACKATHON.md). A figure whose script is a SQL file in that folder can be recomputed live: only a file
/// numbers.json names, run as a database user that can only read (<c>evidence_reader</c>, db_datareader, never the app's
/// own login), with a 60 s timeout, and compared cell by cell with what the build stored.
/// </summary>
public sealed class EvidenceService
{
    private static readonly Regex IdToken = new(@"\b(E\d{1,2}|B0)\b", RegexOptions.Compiled);
    private readonly EvidenceOptions _options;
    private readonly AgentDataSource _source;
    private readonly string _contentRoot;

    public EvidenceService(IOptions<EvidenceOptions> options, AgentDataSource source, IWebHostEnvironment env)
        : this(options.Value, source, env.ContentRootPath) { }

    public EvidenceService(EvidenceOptions options, AgentDataSource source, string contentRoot)
    {
        _options = options;
        _source = source;
        _contentRoot = contentRoot;
    }

    /// <summary>The evidence folder: configured, or the first docs/hackathon-2026/evidence with a numbers.json above the content root.</summary>
    public string Directory()
    {
        if (!string.IsNullOrWhiteSpace(_options.Directory))
            return Path.GetFullPath(Path.IsPathRooted(_options.Directory) ? _options.Directory : Path.Combine(_contentRoot, _options.Directory));
        for (var dir = new DirectoryInfo(_contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "hackathon-2026", "evidence");
            if (File.Exists(Path.Combine(candidate, "numbers.json"))) return candidate;
        }
        throw new InvalidOperationException("docs/hackathon-2026/evidence/numbers.json not found above the content root; set Evidence:Directory.");
    }

    public (string Generated, string Note, IReadOnlyList<EvidenceFigure> Figures) ReadNumbers()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory(), "numbers.json")));
        var root = doc.RootElement;
        var figures = root.GetProperty("figures").EnumerateArray().Select(f => new EvidenceFigure(
            f.GetProperty("id").GetString()!, Text(f, "name"), f.GetProperty("value").Clone(), Text(f, "unit"), f.GetProperty("label").Clone(),
            Text(f, "method"), Text(f, "source"), Text(f, "script"),
            f.TryGetProperty("recompute", out var r) ? new EvidenceRecompute(r.GetProperty("file").GetString()!, r.GetProperty("checks").EnumerateArray().Select(c =>
                new EvidenceCheck(Text(c, "label"), c.GetProperty("set").GetInt32(), Text(c, "column"),
                    c.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.Object
                        ? w.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())
                        : null,
                    c.TryGetProperty("agg", out var a) ? a.GetString() : null,
                    c.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null)).ToList()) : null,
            f.TryGetProperty("sinceRegistration", out var s) ? s.GetString() : null)).ToList();
        return (Text(root, "generated"), Text(root, "note"), figures);

        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    }

    public async Task<EvidencePage> PageAsync(CancellationToken ct = default)
    {
        var (generated, note, figures) = ReadNumbers();
        var dir = Directory();
        var uses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Use(string id, string where)
        {
            if (!uses.TryGetValue(id, out var list)) uses[id] = list = new List<string>();
            // At most 10 per document, so a figure named often in one document still shows the others.
            var doc = where.StartsWith("sheet ", StringComparison.Ordinal) ? "sheet " : where[..(where.IndexOf(" line ", StringComparison.Ordinal) + 1)];
            if (list.Count(u => u.StartsWith(doc, StringComparison.Ordinal)) < 10 && !list.Contains(where)) list.Add(where);
        }
        foreach (var (file, label) in new[] { (Path.Combine(dir, "..", "one-pager.md"), "one-pager"), (Path.Combine(dir, "..", "..", "..", "HACKATHON.md"), "HACKATHON.md") })
        {
            if (!File.Exists(file)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in IdToken.Matches(lines[i])) Use(m.Value, $"{label} line {i + 1}");
        }
        var sheet = Path.Combine(dir, "..", "Financial sheet.xlsx");
        if (File.Exists(sheet))
            foreach (var (id, cell) in SheetCells(sheet)) Use(id, $"sheet {cell}");

        Dictionary<string, DateTime> loads = new(StringComparer.OrdinalIgnoreCase);
        string? loadsError = null;
        try
        {
            await using var conn = await OpenAsReaderAsync(ct);
            await using var cmd = new SqlCommand("SELECT TableName, MAX(ExtractedAtUtc) FROM hk.DataSources GROUP BY TableName", conn);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) loads[rd.GetString(0)] = rd.GetDateTime(1);
        }
        catch (SqlException ex) { loadsError = ex.Message; }
        return new EvidencePage(generated, note, figures, uses.ToDictionary(u => u.Key, u => (IReadOnlyList<string>)u.Value), loads, loadsError);
    }

    /// <summary>The hk tables a figure rests on (an agent view counts as the hk table under it) and when each was loaded.</summary>
    public static IReadOnlyList<(string Table, DateTime? LoadedUtc)> SourcesOf(EvidenceFigure f, IReadOnlyDictionary<string, DateTime> loads)
    {
        var names = Regex.Matches(f.Source + " " + f.Method, @"\b(hk|agent)\.([A-Za-z0-9_]+)").Select(m => "hk." + m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Select(n => (n, loads.TryGetValue(n, out var t) ? t : (DateTime?)null)).Where(x => x.Item2 is not null).ToList();
    }

    /// <summary>Runs one recomputable file and compares its cells with the stored values. Refuses any file numbers.json doesn't name.</summary>
    public async Task<RecomputeResult> RecomputeAsync(string file, CancellationToken ct = default)
    {
        var figures = ReadNumbers().Figures.Where(f => f.Recompute is { } r && string.Equals(r.File, file, StringComparison.Ordinal)).ToList();
        if (figures.Count == 0 || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{file}' isn't a recomputable file named in numbers.json.");
        var dir = Directory();
        var path = Path.GetFullPath(Path.Combine(dir, file));
        if (!string.Equals(Path.GetDirectoryName(path), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{file}' isn't in the evidence folder.");
        var sql = await File.ReadAllTextAsync(path, ct);

        var sw = Stopwatch.StartNew();
        var sets = new DataSet();
        await using (var conn = await OpenAsReaderAsync(ct))
        await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.TimeoutSeconds })
            using (var adapter = new SqlDataAdapter(cmd)) adapter.Fill(sets);
        var ms = sw.ElapsedMilliseconds;
        var lines = figures.SelectMany(f => f.Recompute!.Checks.Select(c => new RecomputeLine(f.Id, c.Label, c.Value, Cell(sets, c)))).ToList();
        return new RecomputeResult(file, ms, lines);
    }

    /// <summary>The same cell build-numbers.ps1 read (its CellValue), rounded to 2 places.</summary>
    public static decimal? Cell(DataSet sets, EvidenceCheck c)
    {
        if (c.Set >= sets.Tables.Count) return null;
        var rows = sets.Tables[c.Set].Rows.Cast<DataRow>();
        if (c.Where is { } where)
            foreach (var (col, value) in where)
                rows = rows.Where(r => string.Equals(Convert.ToString(r[col], CultureInfo.InvariantCulture), value, StringComparison.Ordinal));
        var list = rows.ToList();
        decimal Num(DataRow r) => r[c.Column] is DBNull ? 0m : Convert.ToDecimal(r[c.Column], CultureInfo.InvariantCulture);
        if (c.Agg == "sum") return Math.Round(list.Sum(Num), 2);
        return list.Count == 0 ? null : Math.Round(Num(list[0]), 2);
    }

    /// <summary>A connection that can only read: the app's connection impersonating <see cref="EvidenceOptions.ReaderUser"/>. Not
    /// pooled: a pooled connection would go back with the impersonation on, and the next user of it fails.</summary>
    private async Task<SqlConnection> OpenAsReaderAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(new SqlConnectionStringBuilder(_source.ConnectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using var impersonate = new SqlCommand($"EXECUTE AS USER = '{_options.ReaderUser.Replace("'", "''")}';", conn);
        await impersonate.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>The cells of the financial sheet that name an ID (sheet!cell), read from the xlsx itself.</summary>
    public static IEnumerable<(string Id, string Cell)> SheetCells(string xlsx)
    {
        using var zip = ZipFile.OpenRead(xlsx);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XDocument Load(string name) { using var s = zip.GetEntry(name)!.Open(); return XDocument.Load(s); }
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? new List<string>()
            : Load("xl/sharedStrings.xml").Root!.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
        var targets = Load("xl/_rels/workbook.xml.rels").Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        var found = new List<(string, string)>();
        foreach (var sheet in Load("xl/workbook.xml").Root!.Descendants(ns + "sheet"))
        {
            var target = targets[(string)sheet.Attribute(rel + "id")!].TrimStart('/');
            var entry = target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
            foreach (var c in Load(entry).Descendants(ns + "c"))
            {
                var text = (string?)c.Attribute("t") switch
                {
                    "s" => int.TryParse(c.Element(ns + "v")?.Value, out var i) && i < shared.Count ? shared[i] : null,
                    "inlineStr" => string.Concat(c.Descendants(ns + "t").Select(t => t.Value)),
                    _ => null,
                };
                if (text is null) continue;
                foreach (Match m in IdToken.Matches(text)) found.Add((m.Value, $"{sheet.Attribute("name")!.Value}!{c.Attribute("r")!.Value}"));
            }
        }
        return found;
    }

    /// <summary>A stored value as the page shows it: central (low / high) where it has them, else its fields.</summary>
    public static string Show(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => Num(v),
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
        JsonValueKind.Array => $"{v.GetArrayLength()} rows",
        JsonValueKind.Object when Has(v, "central") && Has(v, "low") && Has(v, "high") =>
            $"{Num(v.GetProperty("central"))} ({Num(v.GetProperty("low"))} / {Num(v.GetProperty("high"))})" + Extra(v, "central", "low", "high"),
        JsonValueKind.Object when Has(v, "mid") && Has(v, "low") && Has(v, "high") =>
            $"{Num(v.GetProperty("mid"))} ({Num(v.GetProperty("low"))} / {Num(v.GetProperty("high"))})" + Extra(v, "mid", "low", "high"),
        JsonValueKind.Object => string.Join("; ", v.EnumerateObject().Take(8).Select(p => $"{p.Name}: {Short(p.Value)}")),
        _ => "",
    };

    private static string Extra(JsonElement v, params string[] skip)
    {
        var rest = v.EnumerateObject().Where(p => !skip.Contains(p.Name)).Select(p => $"{p.Name}: {Short(p.Value)}").ToList();
        return rest.Count == 0 ? "" : "; " + string.Join("; ", rest);
    }

    private static string Short(JsonElement v, int depth = 0) => v.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", v.EnumerateObject().Take(4).Select(p =>
            $"{p.Name}: {(p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && depth > 0 ? "…" : Short(p.Value, depth + 1))}")) + (v.EnumerateObject().Count() > 4 ? ", …" : "") + "}",
        JsonValueKind.Array => $"{v.GetArrayLength()} rows",
        JsonValueKind.Number => Num(v),
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Null => "n/a",
        _ => v.GetRawText(),
    };

    private static bool Has(JsonElement v, string name) => v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number;

    private static string Num(JsonElement v) =>
        v.TryGetDecimal(out var d) ? (d == Math.Round(d) ? d.ToString("N0", CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture)) : v.GetRawText();

    /// <summary>The label: one, or one per case.</summary>
    public static string ShowLabel(JsonElement label) => label.ValueKind == JsonValueKind.Object
        ? string.Join("; ", label.EnumerateObject().Select(p => $"{p.Name}: {p.Value.GetString()}"))
        : label.GetString() ?? "";
}
