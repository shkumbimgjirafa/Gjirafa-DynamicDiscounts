using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PricingTool.Data;
using PricingTool.Data.Entities;
using PricingTool.Data.Services;

namespace PricingTool.Web.Controllers;

/// <summary>
/// Manager-only administration of the Porta authorization allowlist: add users, change their role,
/// and activate/deactivate access. Mirrors the sibling Finance app's admin user-management screen.
/// Porta authenticates; this is where their Analyst/Manager role is decided.
/// </summary>
[Authorize(Roles = PricingRoles.Manager)]
public class UsersController : Controller
{
    private readonly PricingToolDbContext _db;
    private readonly AuditService _audit;

    public UsersController(PricingToolDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var users = await _db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync();
        return View(users);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string email, string? name, string role)
    {
        email = (email ?? "").Trim().ToLowerInvariant();

        if (email.Length == 0 || !email.Contains('@'))
        {
            TempData["Error"] = "Enter a valid email address.";
            return RedirectToAction(nameof(Index));
        }
        if (!PricingRoles.IsValid(role))
        {
            TempData["Error"] = "Pick a valid role (Analyst or Manager).";
            return RedirectToAction(nameof(Index));
        }
        if (await _db.Users.AnyAsync(u => u.Email == email))
        {
            TempData["Error"] = $"{email} is already on the list.";
            return RedirectToAction(nameof(Index));
        }

        _db.Users.Add(new User
        {
            Email = email,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Role = role,
            Active = true,
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(CurrentUser, AuditCategories.Config, "Added user",
            entityType: nameof(User), entityId: email, newValue: role);

        TempData["Message"] = $"Added {email} as {role}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(int id, string role)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        if (!PricingRoles.IsValid(role))
        {
            TempData["Error"] = "Pick a valid role (Analyst or Manager).";
            return RedirectToAction(nameof(Index));
        }
        // Don't let the last active Manager demote themselves out of the role and lock everyone out.
        if (user.Role == PricingRoles.Manager && role != PricingRoles.Manager
            && !await OtherActiveManagerExists(user.Id))
        {
            TempData["Error"] = "Can't demote the last active Manager.";
            return RedirectToAction(nameof(Index));
        }

        var old = user.Role;
        user.Role = role;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(CurrentUser, AuditCategories.Config, "Changed user role",
            entityType: nameof(User), entityId: user.Email, oldValue: old, newValue: role);

        TempData["Message"] = $"{user.Email} is now {role}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id, bool active)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        // Don't let the last active Manager deactivate themselves and lock everyone out.
        if (!active && user.Role == PricingRoles.Manager && !await OtherActiveManagerExists(user.Id))
        {
            TempData["Error"] = "Can't deactivate the last active Manager.";
            return RedirectToAction(nameof(Index));
        }

        user.Active = active;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(CurrentUser, AuditCategories.Config,
            active ? "Activated user" : "Deactivated user",
            entityType: nameof(User), entityId: user.Email);

        TempData["Message"] = $"{user.Email} {(active ? "activated" : "deactivated")}.";
        return RedirectToAction(nameof(Index));
    }

    private Task<bool> OtherActiveManagerExists(int excludeId) =>
        _db.Users.AnyAsync(u => u.Id != excludeId && u.Active && u.Role == PricingRoles.Manager);

    private string CurrentUser =>
        User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "unknown";
}
