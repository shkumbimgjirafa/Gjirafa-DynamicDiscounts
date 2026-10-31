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
