using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// The Evidence page (plan section 5, demo step 5; Prompt 11): every figure of numbers.json with its label, method, sources and
/// where it is used, and a live recompute of the figures whose script is a SQL file (<see cref="EvidenceService"/>).
/// </summary>
[Authorize(Roles = "Analyst,Manager")]
public class EvidenceController : Controller
{
    private readonly EvidenceService _evidence;
    public EvidenceController(EvidenceService evidence) => _evidence = evidence;

    public async Task<IActionResult> Index(CancellationToken ct) => View(await _evidence.PageAsync(ct));

    /// <summary>Runs one file numbers.json names, read-only, and answers with each compared cell: stored, live, and the time taken.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Recompute(string file, CancellationToken ct)
    {
        try
        {
            return Json(await _evidence.RecomputeAsync(file, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
