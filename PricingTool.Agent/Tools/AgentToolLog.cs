using PricingTool.Agent.Storage;

namespace PricingTool.Agent.Tools;

/// <summary>Writes one <c>ai.AgentQueryLog</c> row per data-tool call, refused or run.</summary>
public sealed class AgentToolLog
{
    private const int MaxRequestChars = 8_000;
    private const int MaxErrorChars = 2_000;
    private readonly AgentDbContext _db;
    private readonly TimeProvider _clock;

    public AgentToolLog(AgentDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task WriteAsync(AgentToolContext ctx, string tool, string request, bool accepted, int? rowCount, bool truncated,
        TimeSpan duration, string? error)
    {
        _db.AgentQueryLog.Add(new AgentQueryLogEntry
        {
            CalledAtUtc = _clock.GetUtcNow().UtcDateTime - duration,
            Agent = ctx.Agent.ToString(),
            CallTag = ctx.CallTag,
            Tool = tool,
            Request = request.Length > MaxRequestChars ? request[..MaxRequestChars] : request,
            Accepted = accepted,
            RowCount = rowCount,
            Truncated = truncated,
            DurationMs = (int)duration.TotalMilliseconds,
            Error = error is { Length: > MaxErrorChars } ? error[..MaxErrorChars] : error,
        });
        await _db.SaveChangesAsync(CancellationToken.None);
    }
}
