using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PricingTool.Web.Services;

/// <summary>
/// The evidence as one self-contained HTML file (the user's decision, 31 Oct: proof lives in files, not in the live app):
/// every figure of numbers.json with its label, method, sources and where it is used, the claim from the financial sheet,
/// and every SQL figure recomputed while the file is written, read-only as <c>evidence_reader</c>, compared cell by cell
/// with the stored value. Written by <c>PricingTool.Web --evidence-html &lt;file&gt;</c>; nothing in it is typed by hand.
/// </summary>
public static class EvidenceHtml
{
    /// <summary>The plan's list (section 5, "Evidence page"): what each item is, as references to numbers.json.</summary>
    public static readonly (string Item, string[] Ids)[] PlanList =
    {
        ("The calculator's Aug–Sep lift (B0): E20 mid is the claim's rate; E20 low and E27 are robustness checks; E20 high is sensitivity, quoted", new[] { "E20", "E27" }),
        ("The same-weeks control (changed vs unchanged products in the same weeks): a robustness check, E20 low", new[] { "E20" }),
        ("B0 against untouched products in the same category and weeks: a robustness check", new[] { "E27" }),
        ("The test, run by hand: the facts beside the without-the-build line (assumed)", new[] { "E28" }),
        ("The year-over-year margin fingerprint", new[] { "E25" }),
        ("Për ty Oct 2026: the agents' plan vs the hand plan's forecast vs what it made", new[] { "E11", "E12" }),
        ("Cross-dock lines sold under booked cost, last 12 months", new[] { "E14" }),
        ("Below-floor gross profit forgone, by cause", new[] { "E21" }),
        ("Sales under the floor: their share of revenue and of gross profit", new[] { "E30" }),
        ("The Kosovo gross-profit base", new[] { "E13" }),
        ("The backtest result (the levers in the claim)", new[] { "E8", "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E24" }),
        ("The Sales Report tie-out (each approval's 30 days before: the tool vs the exported lines)", new[] { "E26" }),
    };

    public static async Task<string> RenderAsync(EvidenceService evidence, CancellationToken ct = default)
    {
        var page = await evidence.PageAsync(ct);
        var written = DateTime.Now;
        var results = new Dictionary<string, RecomputeResult>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in page.Figures.Where(f => f.Recompute is not null).Select(f => f.Recompute!.File).Distinct())
        {
            try { results[file] = await evidence.RecomputeAsync(file, ct); }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or ArgumentException or IOException) { errors[file] = ex.Message; }
        }
        var lines = results.Values.SelectMany(r => r.Lines).ToList();
        var byId = page.Figures.ToDictionary(f => f.Id);
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string Head(string id) => !byId.TryGetValue(id, out var f) ? "not in numbers.json"
            : f.Value.ValueKind == JsonValueKind.Object && (f.Value.TryGetProperty("mid", out _) || f.Value.TryGetProperty("central", out _))
                ? H(EvidenceService.Show(f.Value).Split("; ")[0]) : EvidenceService.ShowHtml(f.Value);

        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Pricing Tool v2 evidence</title>
            <style>
              :root { --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --line: #d8dee4; --soft: #f6f8fa; --ok: #1a7f37; --bad: #cf222e; --accent: #0969da; }
              @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #8d96a0; --line: #30363d; --soft: #161b22; --ok: #3fb950; --bad: #f85149; --accent: #4493f8; } }
              body { background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; margin: 0; padding: 24px 16px 64px; }
              main { max-width: 1200px; margin: 0 auto; }
              h1 { font-size: 1.6rem; margin: 0 0 4px; } h2 { font-size: 1.15rem; margin: 32px 0 8px; }
              .muted { color: var(--muted); } .small { font-size: 0.85rem; }
              .claim { font-size: 1.2rem; padding: 12px 16px; border: 1px solid var(--line); border-radius: 8px; background: var(--soft); margin: 16px 0; }
              .summary { display: flex; flex-wrap: wrap; gap: 12px; margin: 12px 0; }
              .pill { border: 1px solid var(--line); border-radius: 999px; padding: 2px 12px; font-size: 0.9rem; }
              .ok { color: var(--ok); } .bad { color: var(--bad); }
              .scroll { overflow-x: auto; }
              table { border-collapse: collapse; width: 100%; font-size: 0.9rem; }
              th, td { text-align: left; vertical-align: top; padding: 6px 8px; border-bottom: 1px solid var(--line); }
              th { background: var(--soft); position: sticky; top: 0; }
              td.num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
              code { font-size: 0.85em; background: var(--soft); padding: 1px 4px; border-radius: 4px; word-break: break-all; }
              a { color: var(--accent); } ul { margin: 0; padding-left: 18px; }
              details summary { cursor: pointer; color: var(--muted); }
              .label { display: inline-block; padding: 0 8px; border-radius: 999px; border: 1px solid var(--line); font-size: 0.8rem; white-space: nowrap; }
            </style></head><body><main>
            """);
        sb.Append("<h1>Pricing Tool v2: the evidence</h1>");
        sb.Append($"<div class=\"muted small\">Written {written:ddd d MMM yyyy, HH:mm} from <code>docs/hackathon-2026/evidence/numbers.json</code> (built {H(page.Generated)} by <code>build-numbers.ps1</code>). " +
                  "Nothing here is typed by hand. To write it again: <code>dotnet run --project PricingTool.Web --no-build --launch-profile hackathon -- --evidence-html docs/hackathon-2026/evidence/evidence.html</code></div>");
        if (page.Claim is { } claim)
        {
            var mid = byId.TryGetValue("E20", out var e20) && e20.Value.ValueKind == JsonValueKind.Object && e20.Value.TryGetProperty("mid", out var m) ? "€" + m.GetDecimal().ToString("N0", CultureInfo.InvariantCulture) : "n/a";
            sb.Append($"<div class=\"claim\"><strong>The claim: €{claim.Monthly.ToString("N0", CultureInfo.InvariantCulture)} a month at full scope, {claim.Score:0} points</strong> " +
                      $"<span class=\"small muted\">(year 1 net benefit €{claim.Net.ToString("N0", CultureInfo.InvariantCulture)}; B0 {mid} a month, E20 mid). From the financial sheet's Summary; the figures below are what it rests on.</span></div>");
        }
        var matched = lines.Count(l => l.Matches);
        sb.Append("<div class=\"summary\">");
        sb.Append($"<span class=\"pill\">{page.Figures.Count} figures</span>");
        sb.Append($"<span class=\"pill {(matched == lines.Count && errors.Count == 0 ? "ok" : "bad")}\">Recomputed now: {matched} of {lines.Count} checks match, {results.Count} SQL files, " +
                  $"{results.Values.Sum(r => r.Ms) / 1000.0:0.0} s, read-only as evidence_reader</span>");
        if (errors.Count > 0) sb.Append($"<span class=\"pill bad\">{errors.Count} file(s) couldn't run</span>");
        if (page.LoadsError is { } le) sb.Append($"<span class=\"pill bad\">hk.DataSources couldn't be read: {H(le)}</span>");
        sb.Append("</div>");
        sb.Append($"<p class=\"small muted\">{H(page.Note)}</p>");

        sb.Append("<h2>The plan's list</h2><div class=\"scroll\"><table><thead><tr><th>Item</th><th>Figure</th><th>Value (central, low / high)</th></tr></thead><tbody>");
        foreach (var (item, ids) in PlanList)
            sb.Append($"<tr><td>{H(item)}</td><td>{string.Join(" ", ids.Select(i => $"<a href=\"#{i}\">{i}</a>"))}</td><td class=\"small\">{Head(ids[0])}" +
                      (ids[0] == "E20" && item.StartsWith("The calculator", StringComparison.Ordinal) && byId.TryGetValue("E20", out var f20) && f20.SinceRegistration is { } why
                          ? $"<div class=\"muted\">Since the registration: {H(why)}</div>" : "") + "</td></tr>");
        sb.Append("</tbody></table></div>");

        sb.Append($"<h2>Every figure ({page.Figures.Count})</h2><div class=\"scroll\"><table><thead><tr><th>ID</th><th>Figure</th><th>Value</th><th>Label</th><th>Method and sources</th><th>Used in</th><th>Recomputed now</th></tr></thead><tbody>");
        foreach (var f in page.Figures)
        {
            var uses = page.Uses.TryGetValue(f.Id, out var u) ? u : Array.Empty<string>();
            sb.Append($"<tr id=\"{H(f.Id)}\"><td><strong>{H(f.Id)}</strong></td><td>{H(f.Name)}<div class=\"small muted\">{H(f.Unit)}</div></td>");
            sb.Append($"<td class=\"small\">{EvidenceService.ShowHtml(f.Value)}</td><td><span class=\"label\">{H(EvidenceService.ShowLabel(f.Label))}</span></td>");
            sb.Append($"<td class=\"small\"><details><summary>method</summary>{H(f.Method)}</details><div>Source: {H(f.Source)}</div>");
            foreach (var (table, loaded) in EvidenceService.SourcesOf(f, page.Loads))
                sb.Append($"<div class=\"muted\">{H(table)} loaded {loaded!.Value:yyyy-MM-dd HH:mm} UTC (hk.DataSources)</div>");
            sb.Append($"<div>Script: <code>{H(f.Script)}</code></div></td><td class=\"small\">");
            if (uses.Count == 0) sb.Append("—");
            foreach (var g in uses.GroupBy(x => x.StartsWith("sheet ", StringComparison.Ordinal) ? "sheet" : x[..x.IndexOf(" line ", StringComparison.Ordinal)]))
                sb.Append($"<div><strong>{H(g.Key)}</strong>: {H(string.Join(", ", g.Select(x => g.Key == "sheet" ? x["sheet ".Length..] : x[(x.IndexOf(" line ", StringComparison.Ordinal) + 6)..])))}</div>");
            sb.Append("</td><td class=\"small\">");
            if (f.Recompute is { } r)
            {
                if (errors.TryGetValue(r.File, out var err)) sb.Append($"<div class=\"bad\">couldn't run <code>{H(r.File)}</code>: {H(err)}</div>");
                else
                {
                    sb.Append($"<div><code>{H(r.File)}</code></div><table><tr><th>check</th><th>stored</th><th>now</th></tr>");
                    foreach (var l in lines.Where(l => l.FigureId == f.Id))
                        sb.Append($"<tr><td>{H(l.Label)}</td><td class=\"num\">{l.Stored?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a"}</td>" +
                                  $"<td class=\"num {(l.Matches ? "ok" : "bad")}\">{l.Live?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a"}{(l.Matches ? " ✓" : " ✗")}</td></tr>");
                    sb.Append("</table>");
                }
            }
            else if (f.Source.StartsWith("ai.BacktestSnapshot", StringComparison.Ordinal))
                sb.Append($"<div>Stored run: {H(f.Source)}</div><div class=\"muted\">Recompute: <code>{H(f.Script)}</code> (about 90 s; not run here)</div>");
            else sb.Append($"<div class=\"muted\">Not a SQL file: <code>{H(f.Script)}</code></div>");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></main></body></html>");
        return sb.ToString();
    }
}
