namespace PricingTool.AgentTools;

/// <summary>A clock stopped at a past moment, for a run that must see the data as of then (the Për ty reproduction test).</summary>
public sealed class AsOfClock : TimeProvider
{
    private readonly DateTimeOffset _now;
    public AsOfClock(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
}
