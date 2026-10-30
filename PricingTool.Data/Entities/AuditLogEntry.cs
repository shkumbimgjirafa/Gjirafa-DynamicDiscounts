namespace PricingTool.Data.Entities;

public static class AuditCategories
{
    public const string Config = "Config";
    public const string Push = "Push";
    public const string Run = "Run";
    public const string Review = "Review";
}

/// <summary>Who / what / when / old / new — for every configuration change and every price push (rule 4).</summary>
public class AuditLogEntry
{
    public long Id { get; set; }

    /// <summary>The layer this entry relates to; null for global (non-layer-specific) actions.</summary>
    public int? LayerId { get; set; }

    public DateTime TimestampUtc { get; set; }
    public string UserName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
