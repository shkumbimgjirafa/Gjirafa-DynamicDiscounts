using PricingTool.Data.Entities;

namespace PricingTool.Data.Services;

/// <summary>Single write path for the audit log (architecture rule 4).</summary>
public class AuditService
{
    private readonly PricingToolDbContext _db;

    public AuditService(PricingToolDbContext db) => _db = db;

    public async Task LogAsync(
        string userName, string category, string action,
        string? entityType = null, string? entityId = null,
        string? oldValue = null, string? newValue = null,
        int? layerId = null,
        CancellationToken ct = default)
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            LayerId = layerId,
            TimestampUtc = DateTime.UtcNow,
            UserName = userName,
            Category = category,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValue = Truncate(oldValue),
            NewValue = Truncate(newValue),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value) =>
        value is { Length: > 3500 } ? value[..3500] + "…" : value;
}
