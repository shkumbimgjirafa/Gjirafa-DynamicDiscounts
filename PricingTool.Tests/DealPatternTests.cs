using PricingTool.NopIntegration;

namespace PricingTool.Tests;

/// <summary>The name patterns a person protects team discounts with in the chat ("protect-discount").</summary>
public class DealPatternTests
{
    [Theory]
    [InlineData("dd-", "dd-12345mo", true)]                    // no wildcard: a name prefix
    [InlineData("dd-", "DD-12345MO", true)]                    // case ignored
    [InlineData("dd-", "oct-dd-12345mo", false)]
    [InlineData("dd-<sku>", "dd-12345mo", true)]               // the user's own words: "dd-sku"
    [InlineData("dd-{sku}", "dd-12345mo", true)]
    [InlineData("*techtober*", "techtober-2026-12345mo", true)]
    [InlineData("oct-%-26-*", "oct-deals-26-12345mo", true)]
    [InlineData("oct-%-26-*", "nov-deals-26-12345mo", false)]
    [InlineData("a.b", "axb-1", false)]                        // a dot is a dot, not any character
    [InlineData("  ", "anything", false)]
    public void A_pattern_matches_as_described(string pattern, string name, bool matches) =>
        Assert.Equal(matches, DealPattern.Matches(pattern, name));
}
