using System.Text.Json;

namespace PricingTool.Agent.ClaudeCode;

/// <summary>
/// The single JSON object <c>claude -p --output-format json</c> prints: the answer (<c>result</c>, and
/// <c>structured_output</c> when <c>--json-schema</c> was given) plus what the call cost.
/// </summary>
internal sealed class ClaudeCodeEnvelope
{
    public bool IsError { get; private init; }
    public string? Subtype { get; private init; }
    public string? Result { get; private init; }
    public JsonElement? StructuredOutput { get; private init; }
    public int? DurationMs { get; private init; }
    public int? DurationApiMs { get; private init; }
    public int? NumTurns { get; private init; }
    public decimal? TotalCostUsd { get; private init; }
    public int InputTokens { get; private init; }
    public int OutputTokens { get; private init; }
    public int CacheReadTokens { get; private init; }
    public int CacheCreationTokens { get; private init; }
    public IReadOnlyList<string> ModelIds { get; private init; } = Array.Empty<string>();

    /// <summary>Parses stdout; null when it holds no result object (Claude Code failed before answering).</summary>
    public static ClaudeCodeEnvelope? TryParse(string stdout)
    {
        var text = stdout.Trim();
        if (text.Length == 0) return null;

        // Normally stdout is exactly one object; if anything precedes it, the object is the last line.
        if (!TryParseObject(text, out var root))
        {
            var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
            if (!TryParseObject(last, out root)) return null;
        }
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "result") return null;

        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var models = root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
            ? mu.EnumerateObject().Select(p => p.Name).ToList()
            : new List<string>();

        return new ClaudeCodeEnvelope
        {
            IsError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
            Subtype = Str(root, "subtype"),
            Result = Str(root, "result"),
            StructuredOutput = root.TryGetProperty("structured_output", out var so) && so.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? so.Clone() : null,
            DurationMs = Int(root, "duration_ms"),
            DurationApiMs = Int(root, "duration_api_ms"),
            NumTurns = Int(root, "num_turns"),
            TotalCostUsd = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null,
            InputTokens = usage.ValueKind == JsonValueKind.Object ? Int(usage, "input_tokens") ?? 0 : 0,
            OutputTokens = usage.ValueKind == JsonValueKind.Object ? Int(usage, "output_tokens") ?? 0 : 0,
            CacheReadTokens = usage.ValueKind == JsonValueKind.Object ? Int(usage, "cache_read_input_tokens") ?? 0 : 0,
            CacheCreationTokens = usage.ValueKind == JsonValueKind.Object ? Int(usage, "cache_creation_input_tokens") ?? 0 : 0,
            ModelIds = models,
        };
    }

    private static bool TryParseObject(string text, out JsonElement root)
    {
        root = default;
        if (!text.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
