using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PricingTool.Agent.Tools;
using PricingTool.Web.Controllers;
using PricingTool.Web.Services;

namespace PricingTool.Tests;

/// <summary>The Evidence page (Prompt 11): every figure of the real numbers.json, and a recompute that runs only a file numbers.json
/// names, as a user that can only read.</summary>
public class EvidenceTests : IClassFixture<LocalDbFixture>
{
    private readonly LocalDbFixture _fixture;
    public EvidenceTests(LocalDbFixture fixture) => _fixture = fixture;

    /// <summary>The repo's own evidence folder, found above the test's folder as the web app finds it above its content root.</summary>
    private static EvidenceService RepoEvidence() =>
        new(new EvidenceOptions(), new AgentDataSource(LocalDb.Server + "Database=PricingToolNoSuchDb;Connect Timeout=2;"), AppContext.BaseDirectory);

    [Fact]
    public async Task The_page_lists_every_figure_of_numbers_json()
    {
        var evidence = RepoEvidence();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence.Directory(), "numbers.json")));
        var ids = doc.RootElement.GetProperty("figures").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();

        var view = Assert.IsType<ViewResult>(await new EvidenceController(evidence).Index(CancellationToken.None));

        var page = Assert.IsType<EvidencePage>(view.Model);
        Assert.Equal(ids, page.Figures.Select(f => f.Id));
        Assert.All(page.Figures, f => Assert.False(string.IsNullOrEmpty(EvidenceService.Show(f.Value))));
        Assert.Contains("E20", ids);
        Assert.NotNull(page.Figures.Single(f => f.Id == "E20").Recompute);
        Assert.Contains(page.Uses["E20"], u => u.StartsWith("sheet ", StringComparison.Ordinal));                 // the financial sheet names it
    }

    /// <summary>A sheet's cells (cached values; shared strings resolved), read from the xlsx itself.</summary>
    private static Dictionary<string, string> SheetValues(string xlsx, string sheetName)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(xlsx);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        System.Xml.Linq.XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        System.Xml.Linq.XDocument Load(string name) { using var s = zip.GetEntry(name)!.Open(); return System.Xml.Linq.XDocument.Load(s); }
        var shared = Load("xl/sharedStrings.xml").Root!.Elements(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
        var rid = (string)Load("xl/workbook.xml").Root!.Descendants(ns + "sheet").Single(x => (string)x.Attribute("name")! == sheetName).Attribute(rel + "id")!;
        var target = (string)Load("xl/_rels/workbook.xml.rels").Root!.Elements().Single(e => (string)e.Attribute("Id")! == rid).Attribute("Target")!;
        return Load(target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target.TrimStart('/')).Descendants(ns + "c")
            .Where(c => c.Element(ns + "v") is not null)
            .ToDictionary(c => (string)c.Attribute("r")!, c => (string?)c.Attribute("t") == "s" ? shared[int.Parse(c.Element(ns + "v")!.Value)] : c.Element(ns + "v")!.Value);
    }

    [Fact]
    public async Task The_one_claim_is_the_same_in_numbers_json_the_sheet_the_one_pager_HACKATHON_and_the_page()
    {
        var evidence = RepoEvidence();
        var dir = evidence.Directory();
        var figures = evidence.ReadNumbers().Figures;
        var e20 = figures.Single(f => f.Id == "E20");
        var b0 = new[] { "low", "mid", "high" }.Select(k => e20.Value.GetProperty(k).GetDecimal()).ToArray();
        var byShop = e20.Value.GetProperty("byShopAugSepAvg");
        var (mall, gj50) = (byShop.GetProperty("mallKs").GetDecimal(), byShop.GetProperty("gj50Ks").GetDecimal());
        string Eur(decimal x) => "€" + Math.Round(x).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        decimal Num(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        // The evidence keeps every check: E20 low and high (labelled, never "scenario"), E27, and the split by shop adds up to B0.
        Assert.StartsWith("robustness check", e20.Label.GetProperty("low").GetString());
        Assert.Contains("not part of the claim", e20.Label.GetProperty("high").GetString());
        Assert.InRange(mall + gj50 - b0[1], -1m, 1m);
        var e27 = figures.Single(f => f.Id == "E27");
        Assert.StartsWith("robustness check", e27.Label.GetString());
        Assert.Equal(3, e27.Value.GetProperty("total").EnumerateObject().Count());                  // all three columns, the €2.7k one too
        Assert.NotNull(e27.Recompute);                                                              // rerun live on the Evidence page
        Assert.All(figures, f => Assert.DoesNotContain("scenario", f.Label.ToString(), StringComparison.OrdinalIgnoreCase));

        var sheet = Path.Combine(dir, "..", "Financial sheet.xlsx");
        var inputs = SheetValues(sheet, "Inputs");
        var b0Rows = new[] { "Same-weeks control", "Before/after on the approvals", "Same, season-adjusted" }
            .Select(label => Num(inputs["B" + inputs.Single(c => c.Key.StartsWith('A') && c.Value.StartsWith(label, StringComparison.Ordinal)).Key[1..]])).ToArray();
        Assert.Equal(b0, b0Rows);                                                                   // the sheet types E20 in
        var caseSheet = SheetValues(sheet, "Financial case");
        Assert.Equal("The claim", caseSheet["B3"]);                                                 // one model column: the claim
        Assert.All(new[] { "D3", "E3", "F3", "G3" }, c => Assert.False(caseSheet.ContainsKey(c)));
        var summary = SheetValues(sheet, "Summary");
        var (claim, score) = (Num(summary["B6"]), Num(summary["B20"]));
        Assert.Equal(Num(caseSheet["B4"]), claim);
        var bySource = SheetValues(sheet, "Benefit by source");
        Assert.Equal(b0[1], Num(bySource["B4"]));
        Assert.Equal(Math.Round(claim, 2), Math.Round(Num(bySource["B11"]), 2));

        var onePager = File.ReadAllText(Path.Combine(dir, "..", "one-pager.md"));
        var hackathon = File.ReadAllText(Path.Combine(dir, "..", "..", "..", "HACKATHON.md"));
        foreach (var doc in new[] { onePager, hackathon })
        {
            // One claim with its score, B0 and B0 by shop; no alternative total, no other B0, no second framing.
            foreach (var x in new[] { claim, b0[1], mall, gj50 }) Assert.Contains(Eur(x), doc);
            Assert.Contains($"{score:0} points", doc);
            Assert.Contains("robustness checks: E20, E27", doc);
            foreach (var x in new[] { b0[0], b0[2] }) Assert.DoesNotContain(Eur(x), doc);
            foreach (var word in new[] { "framing", "cross-check", "controlled measurement", "scenario", "levers alone", "levers only" })
                Assert.DoesNotContain(word, doc, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains($"{Eur(b0[1])[1..]} ({Eur(b0[0])[1..]} / {Eur(b0[2])[1..]})", EvidenceService.Show(e20.Value));
        var page = Assert.IsType<EvidencePage>(Assert.IsType<ViewResult>(await new EvidenceController(evidence).Index(CancellationToken.None)).Model);
        var shown = Assert.NotNull(page.Claim);
        Assert.Equal((Math.Round(claim), score), (Math.Round(shown.Monthly), shown.Score));              // the page's headline
    }

    [Theory]
    [InlineData("not-listed.sql")]
    [InlineData("../one-pager.md")]
    [InlineData(@"..\evidence\e20-test-phase.sql")]
    [InlineData("numbers.json")]
    public async Task A_file_numbers_json_doesnt_name_is_refused(string file)
    {
        var result = await new EvidenceController(RepoEvidence()).Recompute(file, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>A throwaway evidence folder with one figure, its file and the stored cell.</summary>
    private (EvidenceService Service, string Dir) Folder(string sql, decimal stored)
    {
        var dir = Path.Combine(Path.GetTempPath(), "evidence-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "check.sql"), sql);
        File.WriteAllText(Path.Combine(dir, "numbers.json"), JsonSerializer.Serialize(new
        {
            generated = "test", note = "",
            figures = new[] { new { id = "E99", name = "a test figure", value = stored, unit = "EUR", label = "measured", method = "", source = "", script = "check.sql",
                recompute = new { file = "check.sql", checks = new[] { new { label = "x", set = 0, column = "X", value = stored } } } } },
        }));
        using (var c = _fixture.Open())
            new SqlCommand("IF DATABASE_PRINCIPAL_ID('evidence_reader') IS NULL CREATE USER evidence_reader WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER evidence_reader;", c).ExecuteNonQuery();
        return (new EvidenceService(new EvidenceOptions { Directory = dir }, new AgentDataSource(_fixture.ConnectionString), dir), dir);
    }

    [LocalDbFact]
    public async Task A_listed_file_runs_as_the_reader_and_is_compared_with_the_stored_value()
    {
        var (evidence, _) = Folder("SET NOCOUNT ON; SELECT x INTO #t FROM (VALUES (40.5), (2)) v (x); SELECT SUM(x) AS X, USER_NAME() AS U FROM #t;", 42.5m);

        var r = await evidence.RecomputeAsync("check.sql");

        var line = Assert.Single(r.Lines);
        Assert.Equal(("E99", 42.5m, 42.5m, true), (line.FigureId, line.Stored, line.Live, line.Matches));
    }

    [LocalDbFact]
    public async Task A_recompute_cant_write()
    {
        var (evidence, _) = Folder("CREATE TABLE dbo.Written (X int); SELECT 1 AS X;", 1m);

        await Assert.ThrowsAsync<SqlException>(() => evidence.RecomputeAsync("check.sql"));
        using var c = _fixture.Open();
        Assert.Equal(0, (int)new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name = 'Written'", c).ExecuteScalar()!);
    }
}
