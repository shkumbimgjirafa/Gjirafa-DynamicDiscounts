using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Web.Services;

namespace PricingTool.Web.Controllers;

/// <summary>Sets the active layer (Brand + Country) for the session; the whole UI scopes to it.</summary>
[Authorize(Roles = "Analyst,Manager")]
public class LayerController : Controller
{
    private readonly PricingToolDbContext _db;

    public LayerController(PricingToolDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Select(int layerId, string? returnUrl)
    {
        if (await _db.Layers.AnyAsync(l => l.Id == layerId && l.IsActive))
            HttpContext.Session.SetInt32(CurrentLayerService.SessionKey, layerId);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }
}
