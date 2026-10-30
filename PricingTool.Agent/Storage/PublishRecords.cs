namespace PricingTool.Agent.Storage;

/// <summary>One publish of prices to a shop's discount tables (<c>ai.PublishBatch</c>).</summary>
public class PublishBatch
{
    public long Id { get; set; }
    public int LayerId { get; set; }

    /// <summary>The shop database: GjirafaMall / GjirafaEcommerce.</summary>
    public string Db { get; set; } = "";
    public int StoreId { get; set; }

    /// <summary>Sandbox or Live (Publisher:Target when it ran) and the schema it wrote to.</summary>
    public string Target { get; set; } = "";
    public string ShopSchema { get; set; } = "";

    /// <summary>Regular (ai-price-*) or Campaign (ai-campaign-*).</summary>
    public string Kind { get; set; } = "";
    public string? CampaignKey { get; set; }
    public long? EngineRunId { get; set; }

    /// <summary>Running / Published / Failed / RolledBack.</summary>
    public string Status { get; set; } = "";

    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime? RolledBackUtc { get; set; }

    public int Requested { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int RoundTripChecked { get; set; }
    public int RoundTripMismatches { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// The state of one shop row before the publisher touched it (<c>ai.PublishAudit</c>), written before the
/// change, so rollback restores it exactly. Created discounts are expired on rollback, never deleted.
/// </summary>
public class PublishAudit
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string Sku { get; set; } = "";
    public int? ProductId { get; set; }

    /// <summary>DiscountCreated / DiscountUpdated / DiscountExpired / DiscountSuperseded / ProductFlags.</summary>
    public string Change { get; set; } = "";

    public int? DiscountId { get; set; }
    public string? DiscountName { get; set; }
    public decimal? OldDiscountAmount { get; set; }
    public DateTime? OldStartDateUtc { get; set; }
    public DateTime? OldEndDateUtc { get; set; }
    public decimal? NewDiscountAmount { get; set; }
    public DateTime? NewStartDateUtc { get; set; }
    public DateTime? NewEndDateUtc { get; set; }

    public bool? OldHasDiscountsApplied { get; set; }
    public string? OldStoreIds { get; set; }
    public string? NewStoreIds { get; set; }

    /// <summary>False when the shop write it was written for did not happen (the chunk failed).</summary>
    public bool Applied { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RolledBackUtc { get; set; }
}

/// <summary>A SKU the publisher could not or would not publish, or whose price didn't read back
/// (<c>ai.PublishException</c>): what the exceptions inbox lists for a person.</summary>
public class PublishException
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public string Sku { get; set; } = "";

    /// <summary>unresolved-sku / cheaper-deal / at-or-above-shelf / chunk-failed / round-trip.</summary>
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
